namespace Stratara.Abstractions.Domain;

/// <summary>
/// Marker interface for tenant-scoped event-sourced aggregates. The
/// <see cref="TenantId"/> must be stored in the creation event's data and
/// hydrated on aggregate apply, so the framework can authorize cross-tenant
/// access via <c>AggregationServiceTenantExtensions.AggregateOwnedByTenantAsync</c>.
/// </summary>
public interface ITenantAggregate : IAggregate
{
    /// <summary>The Subject (data-owner) tenant id.</summary>
    Guid TenantId { get; set; }
}
