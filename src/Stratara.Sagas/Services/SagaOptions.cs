using System.Diagnostics.CodeAnalysis;

namespace Stratara.Sagas.Services;

/// <summary>Configuration options for the saga subsystem. Bound from the <c>Sagas</c> configuration section.</summary>
[ExcludeFromCodeCoverage]
public sealed class SagaOptions // NOSONAR — used as generic type parameter in AddOptions<SagaOptions>(); cannot be static
{
    /// <summary>Configuration-section name (<c>Sagas</c>) — used by <c>AddOptions&lt;SagaOptions&gt;().Bind(...)</c>.</summary>
    public const string SectionName = "Sagas";

    /// <summary>
    /// Number of parallel consumers the saga worker opens on its subscription. A value that is not a
    /// positive number — including the default of <see langword="null"/> — means one consumer per processor.
    /// Bundles about one aggregate are dispatched one at a time within the process whatever the value; set
    /// it to 1 for a worker that must dispatch every bundle in the order the transport delivers it.
    /// </summary>
    public int? DegreeOfParallelism { get; set; }
}
