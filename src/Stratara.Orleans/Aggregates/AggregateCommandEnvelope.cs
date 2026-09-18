namespace Stratara.Orleans.Aggregates;

/// <summary>
/// A command on its way into the grain of the aggregate it names: the command, its type, and the
/// session it was dispatched under, each as the framework's secure JSON so the grain rebuilds
/// exactly what the caller had. Immutable, so a call inside one silo passes it by reference
/// instead of copying it. A resumed command carries the stamp of the claim that handed it over, which its receiver's
/// first renewal compares, so that of two hand-overs of one claim only one runs; the dispatch's own hand-over, a
/// forwarded command and a hand-over from a silo that does not stamp carry none.
/// </summary>
[GenerateSerializer]
[Alias("Stratara.Orleans.AggregateCommandEnvelope")]
[Immutable]
internal sealed record AggregateCommandEnvelope(
    [property: Id(0)] string CommandTypeName,
    [property: Id(1)] string CommandJson,
    [property: Id(2)] string SessionContextJson,
    [property: Id(3)] DateTimeOffset? ClaimedAt = null);
