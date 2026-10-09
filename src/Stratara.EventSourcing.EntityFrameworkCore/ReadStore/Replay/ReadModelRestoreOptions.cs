using System.Diagnostics.CodeAnalysis;

namespace Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Replay;

/// <summary>
/// Settings of the PostgreSQL read-model preservation that <c>AddReadModelRestore&lt;TReadContext&gt;()</c> registers,
/// bound to the <c>ProjectionReplay:Restore</c> configuration section.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class ReadModelRestoreOptions
{
    /// <summary>Configuration section name (<c>ProjectionReplay:Restore</c>) that hosts these options.</summary>
    public const string SectionName = "ProjectionReplay:Restore";

    /// <summary>
    /// Gets or sets the schema the preserved copies are kept in. Defaults to <c>stratara_replay</c>. The schema is
    /// created on first use, which needs the <c>CREATE</c> privilege on the database; create it beforehand where the
    /// read store's user does not have it.
    /// </summary>
    public string Schema { get; set; } = "stratara_replay";

    /// <summary>
    /// Gets or sets tables the read context maps that a replay does not empty and that are therefore not preserved,
    /// as <c>schema.table</c> or as <c>table</c> in the context's default schema.
    /// </summary>
    public IList<string> ExcludedTables { get; set; } = [];

    /// <summary>
    /// Gets or sets tables the read context does not map that the host's view truncator empties and that are
    /// therefore preserved as well, as <c>schema.table</c> or as <c>table</c> in the context's default schema.
    /// </summary>
    public IList<string> AdditionalTables { get; set; } = [];

    /// <summary>
    /// Gets or sets how long one statement of the preservation or the restoration may take. Copying a large read
    /// store takes as long as reading it; defaults to 30 minutes.
    /// </summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(30);
}
