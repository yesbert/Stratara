namespace Stratara.Orleans.Aggregates;

/// <summary>
/// A command on its way into the grain of the aggregate it names: the command, its type, and the
/// session it was dispatched under, each as the framework's secure JSON so the grain rebuilds
/// exactly what the caller had.
/// </summary>
[GenerateSerializer]
public sealed record AggregateCommandEnvelope(
    [property: Id(0)] string CommandTypeName,
    [property: Id(1)] string CommandJson,
    [property: Id(2)] string SessionContextJson);
