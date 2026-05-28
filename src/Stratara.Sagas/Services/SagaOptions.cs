using System.Diagnostics.CodeAnalysis;

namespace Stratara.Sagas.Services;

/// <summary>Configuration options for the saga subsystem. Bound from the <c>Sagas</c> configuration section.</summary>
[ExcludeFromCodeCoverage]
public sealed class SagaOptions // NOSONAR — used as generic type parameter in AddOptions<SagaOptions>(); cannot be static
{
    /// <summary>Configuration-section name (<c>Sagas</c>) — used by <c>AddOptions&lt;SagaOptions&gt;().Bind(...)</c>.</summary>
    public const string SectionName = "Sagas";
}
