using System.Diagnostics.CodeAnalysis;

namespace Stratara.Projections.Services;

/// <summary>
/// Configuration for the projection workers, bound from the <c>Projections</c> configuration section.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class ProjectionOptions // NOSONAR — used as generic type parameter in AddOptions<ProjectionOptions>(); cannot be static
{
    /// <summary>Configuration section name (<c>Projections</c>) used to bind this options object.</summary>
    public const string SectionName = "Projections";

    /// <summary>Number of event-stream entries fetched per batch during projection replay. Defaults to 5000.</summary>
    public int BatchSize { get; set; } = 5000;

    /// <summary>Idle delay in seconds the worker waits between polls when no work is available. Defaults to 5.</summary>
    public int IdleDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Number of parallel consumers the projection worker opens on its subscription. A value that is not a
    /// positive number — including the default of <see langword="null"/> — means one consumer per processor.
    /// Bundles about one aggregate are applied one at a time within the process whatever the value; set it
    /// to 1 for a worker that must apply every bundle in the order the transport delivers it.
    /// </summary>
    public int? DegreeOfParallelism { get; set; }
}
