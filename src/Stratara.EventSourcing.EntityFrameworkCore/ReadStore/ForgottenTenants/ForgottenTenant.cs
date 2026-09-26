using System.Diagnostics.CodeAnalysis;

namespace Stratara.EventSourcing.EntityFrameworkCore.ReadStore.ForgottenTenants;

/// <summary>
/// One tenant a projection has seen deleted. The row is derived from the deletion fact the projection
/// applied and is emptied with the projection's read model.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class ForgottenTenant
{
    /// <summary>The projection, by the name the framework gives it.</summary>
    public required string Projection { get; set; }

    /// <summary>The deleted tenant.</summary>
    public Guid TenantId { get; set; }
}
