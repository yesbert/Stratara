namespace Stratara.Abstractions.Entities;

/// <summary>
/// Tenant-owning entity. The Subject (data-owner) tenant id participates in EF Core's
/// global query filter so application code never accidentally reads across tenant
/// boundaries.
/// </summary>
/// <remarks>
/// Unprefixed <c>TenantId</c> always means data-owner / Subject — never Actor.
/// </remarks>
public interface IMultiTenant
{
    /// <summary>The Subject (data-owner) tenant id.</summary>
    Guid TenantId { get; set; }
}
