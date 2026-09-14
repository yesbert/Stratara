namespace Stratara.Orleans;

/// <summary>
/// The names of the grain directories the proof of concept's grains select. A host registers a
/// directory under each name; which implementation stands behind it is the host's choice.
/// </summary>
public static class GrainDirectories
{
    /// <summary>
    /// The directory for grains whose single activation must not depend on a calm cluster — the
    /// store readers, singleton work, timer owners, the process manager and the permit grain. A
    /// host backs it with storage (Redis in the proof of concept). Aggregate and runner grains
    /// select no directory and take the host's default, because a duplicate activation of theirs
    /// ends in a concurrency conflict at the store, as it does on the bus path.
    /// </summary>
    public const string Durable = "stratara-durable";
}
