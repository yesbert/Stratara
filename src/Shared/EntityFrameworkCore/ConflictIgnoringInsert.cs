using System.Data.Common;
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
/// for it.
/// </remarks>
internal static class ConflictIgnoringInsert
{
    private static readonly HashSet<string> Providers = new(StringComparer.Ordinal)
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        "Microsoft.EntityFrameworkCore.Sqlite",
    };

    /// <summary>Whether the context's provider can skip a row that is already there.</summary>
    /// <param name="context">The context to write through.</param>
    /// <returns><see langword="true"/> on PostgreSQL and SQLite.</returns>
    public static bool IsSupported(DbContext context) => context.Database.ProviderName is { } provider && Providers.Contains(provider);

    /// <summary>
    /// Inserts one row per entity, in the order given, with every column the context's model maps for the entity's type,
    /// and skips a row whose key or unique value a row already holds. Outside a transaction the statement runs under the
    /// context's execution strategy, since running it again changes nothing; inside one it runs as part of it.
    /// </summary>
    /// <typeparam name="TEntity">The entity type, mapped to a table by the context's model.</typeparam>
    /// <param name="context">A context whose provider <see cref="IsSupported"/> accepts.</param>
    /// <param name="entities">The rows. A caller that inserts several rows a concurrent caller may insert too gives them in one order, so neither statement waits on the other.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>How many rows were inserted; a row skipped for a conflict does not count.</returns>
    /// <exception cref="InvalidOperationException">The context's model maps no table for <typeparamref name="TEntity"/>.</exception>
    public static async Task<int> InsertAsync<TEntity>(DbContext context, IReadOnlyCollection<TEntity> entities, CancellationToken cancellationToken)
        where TEntity : class
    {
        if (entities.Count == 0)
        {
            return 0;
        }

        var entityType = context.Model.FindEntityType(typeof(TEntity))
                         ?? throw new InvalidOperationException($"The model of {context.GetType().Name} has no {typeof(TEntity).Name}.");
        var tableName = entityType.GetTableName()
                        ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not mapped to a table in {context.GetType().Name}.");
        var schema = entityType.GetSchema();
        var table = StoreObjectIdentifier.Table(tableName, schema);
        var columns = entityType.GetProperties()
            .Where(property => !property.IsShadowProperty() && property.GetComputedColumnSql(table) is null)
            .Select(property => (Property: property, Name: property.GetColumnName(table)))
            .Where(column => column.Name is not null)
            .ToList();
        var sql = context.GetService<ISqlGenerationHelper>();

        await using var command = context.Database.GetDbConnection().CreateCommand();
        var parameters = new List<DbParameter>(entities.Count * columns.Count);
        var rows = new List<string>(entities.Count);
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

        var statement = $"INSERT INTO {Literal(sql.DelimitIdentifier(tableName, schema))} " +
                        $"({string.Join(", ", columns.Select(column => Literal(sql.DelimitIdentifier(column.Name!))))}) " +
                        $"VALUES {string.Join(", ", rows)} ON CONFLICT DO NOTHING";

        if (context.Database.CurrentTransaction is not null)
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
