using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Stratara.EntityFrameworkCore;

/// <summary>
/// Inserts rows that another writer may insert at the same moment, on the providers whose insert can skip a row that is
/// already there: PostgreSQL and SQLite. EF Core logs a statement that fails on a key at Error before the caller can
/// catch it, so a race the caller handles would otherwise still read as an error in the log.
/// </summary>
/// <remarks>
/// The file is compiled into each package that writes such rows, so none of them exposes it and none depends on another
/// for it. The insert bypasses <c>SaveChanges</c>: interceptors of saves do not see it, and nothing tracked on the
/// context is saved with it.
/// </remarks>
internal static class ConflictIgnoringInsert
{
    private const int RowsPerStatement = 500;

    private static readonly HashSet<string> Providers = new(StringComparer.Ordinal)
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        "Microsoft.EntityFrameworkCore.Sqlite",
    };

    /// <summary>
    /// Whether <see cref="InsertAsync{TEntity}"/> can write these rows: the provider can skip a row that is already there,
    /// and every column of the table takes its value from a property the rows carry. A table that shares its rows with
    /// another entity type, an entity split across tables, and shadow, complex or owned properties are left to
    /// <c>SaveChanges</c>, and so is a row whose store-generated property still holds its sentinel.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context to write through.</param>
    /// <param name="entities">The rows.</param>
    /// <returns><see langword="true"/> where the insert writes exactly what <c>SaveChanges</c> would.</returns>
    public static bool CanInsert<TEntity>(DbContext context, IReadOnlyCollection<TEntity> entities)
        where TEntity : class
    {
        if (context.Database.ProviderName is not { } provider || !Providers.Contains(provider)
            || context.Model.FindEntityType(typeof(TEntity)) is not { } entityType
            || entityType.GetTableName() is not { } tableName)
        {
            return false;
        }

        var schema = entityType.GetSchema();
        var table = StoreObjectIdentifier.Table(tableName, schema);
        var sharesItsTable = context.Model.GetEntityTypes()
            .Any(other => other != entityType && other.GetTableName() == tableName && other.GetSchema() == schema);
        if (sharesItsTable || entityType.GetMappingFragments().Any() || entityType.GetComplexProperties().Any()
            || entityType.GetNavigations().Any(navigation => navigation.TargetEntityType.IsOwned()))
        {
            return false;
        }

        var properties = entityType.GetProperties().ToList();
        return properties.All(property => !property.IsShadowProperty() && property.GetBeforeSaveBehavior() == PropertySaveBehavior.Save
                                          && property.GetColumnName(table) is not null)
               && entities.All(entity => properties
                   .Where(property => property.ValueGenerated != ValueGenerated.Never)
                   .All(property => !Equals(property.GetGetter().GetClrValue(entity), property.Sentinel)));
    }

    /// <summary>Whether the context takes part in a transaction its caller opened, on the context or around it.</summary>
    /// <param name="context">The context.</param>
    /// <returns><see langword="true"/> where a write on the context runs inside a transaction it did not open itself.</returns>
    public static bool InCallersTransaction(DbContext context) =>
        context.Database.CurrentTransaction is not null || context.Database.GetEnlistedTransaction() is not null || Transaction.Current is not null;

    /// <summary>
    /// Inserts one row per entity, in the order given, with every column the context's model maps for the entity's type,
    /// and skips a row whose key or unique value a row already holds. Outside a transaction each statement runs under the
    /// context's execution strategy; inside one it runs as part of it.
    /// </summary>
    /// <remarks>
    /// Running a statement again changes no row, but it counts none either: where the strategy repeats a statement whose
    /// commit was not acknowledged, the rows it had inserted are counted as skipped.
    /// </remarks>
    /// <typeparam name="TEntity">The entity type, which <see cref="CanInsert{TEntity}"/> accepts.</typeparam>
    /// <param name="context">The context to write through.</param>
    /// <param name="entities">The rows. A caller that inserts several rows a concurrent caller may insert too gives them in one order, so neither statement waits on the other.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>How many rows were inserted; a row skipped for a conflict does not count.</returns>
    /// <exception cref="InvalidOperationException">The context's model maps no table for <typeparamref name="TEntity"/>.</exception>
    public static async Task<int> InsertAsync<TEntity>(DbContext context, IReadOnlyCollection<TEntity> entities, CancellationToken cancellationToken)
        where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity))
                         ?? throw new InvalidOperationException($"The model of {context.GetType().Name} has no {typeof(TEntity).Name}.");
        var tableName = entityType.GetTableName()
                        ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not mapped to a table in {context.GetType().Name}.");
        var schema = entityType.GetSchema();
        var table = StoreObjectIdentifier.Table(tableName, schema);
        var sql = context.GetService<ISqlGenerationHelper>();
        var columns = new List<(IProperty Property, string Name)>();
        foreach (var property in entityType.GetProperties())
        {
            if (property.GetComputedColumnSql(table) is null && property.GetColumnName(table) is { } name)
            {
                columns.Add((property, Literal(sql.DelimitIdentifier(name))));
            }
        }

        var into = $"INSERT INTO {Literal(sql.DelimitIdentifier(tableName, schema))} ({string.Join(", ", columns.Select(column => column.Name))}) VALUES ";
        var inserted = 0;
        foreach (var chunk in entities.Chunk(RowsPerStatement))
        {
            inserted += await InsertChunkAsync(context, into, columns, chunk, cancellationToken);
        }

        return inserted;
    }

    [SuppressMessage("Security Hotspot", "S2077:Formatting SQL queries is security-sensitive",
        Justification = "The statement is composed of the table and column names the context's model maps, each delimited by the " +
                        "provider and with braces doubled, and one placeholder per value; every value is bound as a DbParameter. " +
                        "Nothing a caller supplies reaches the SQL text.")]
    private static async Task<int> InsertChunkAsync<TEntity>(DbContext context, string into, List<(IProperty Property, string Name)> columns, TEntity[] entities,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        var parameters = new List<DbParameter>(entities.Length * columns.Count);
        var rows = new List<string>(entities.Length);
        foreach (var entity in entities)
        {
            var values = new List<string>(columns.Count);
            foreach (var (property, _) in columns)
            {
                values.Add($"{{{parameters.Count}}}");
                parameters.Add(property.GetRelationalTypeMapping()
                    .CreateParameter(command, $"p{parameters.Count}", property.GetGetter().GetClrValue(entity), property.IsNullable));
            }

            rows.Add($"({string.Join(", ", values)})");
        }

        var statement = $"{into}{string.Join(", ", rows)} ON CONFLICT DO NOTHING";
        if (InCallersTransaction(context))
        {
            return await context.Database.ExecuteSqlRawAsync(statement, parameters, cancellationToken);
        }

        return await context.Database.CreateExecutionStrategy().ExecuteAsync(
            (Context: context, Statement: statement, Parameters: parameters),
            static (state, token) => state.Context.Database.ExecuteSqlRawAsync(state.Statement, state.Parameters, token),
            cancellationToken);
    }

    /// <summary>A name as it stands in the statement, which EF Core reads as a format string: a brace is doubled.</summary>
    private static string Literal(string name) => name.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);
}
