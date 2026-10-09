using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Replay;

/// <summary>A table the read-model preservation copies, by schema and name.</summary>
/// <param name="Schema">The table's schema.</param>
/// <param name="Name">The table's name.</param>
internal sealed record PreservedTable(string Schema, string Name)
{
    /// <summary>Reads <c>schema.table</c>, or <c>table</c> in <paramref name="defaultSchema"/>.</summary>
    public static PreservedTable Parse(string name, string defaultSchema)
    {
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? new PreservedTable(defaultSchema, name) : new PreservedTable(name[..dot], name[(dot + 1)..]);
    }

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Schema}.{Name}");
}

/// <summary>Which tables a read context's model asks the preservation to copy.</summary>
internal static class PreservedTables
{
    private const string PostgresDefaultSchema = "public";

    /// <summary>
    /// Every table the model maps — views, keyless types and SQL queries excluded, a table shared by several types
    /// once — minus <see cref="ReadModelRestoreOptions.ExcludedTables"/>, plus
    /// <see cref="ReadModelRestoreOptions.AdditionalTables"/>.
    /// </summary>
    public static List<PreservedTable> Of(IModel model, ReadModelRestoreOptions options)
    {
        var defaultSchema = model.GetDefaultSchema() ?? PostgresDefaultSchema;
        var excluded = options.ExcludedTables.Select(name => PreservedTable.Parse(name, defaultSchema)).ToHashSet();
        return model.GetEntityTypes()
            .Where(type => type.FindPrimaryKey() is not null && type.GetViewName() is null && type.GetSqlQuery() is null)
            .Select(type => type.GetTableName() is { } name ? new PreservedTable(type.GetSchema() ?? defaultSchema, name) : null)
            .OfType<PreservedTable>()
            .Concat(options.AdditionalTables.Select(name => PreservedTable.Parse(name, defaultSchema)))
            .Where(table => !excluded.Contains(table))
            .Distinct()
            .ToList();
    }
}
