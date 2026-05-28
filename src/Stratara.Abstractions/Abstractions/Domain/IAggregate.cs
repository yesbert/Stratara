namespace Stratara.Abstractions.Domain;

/// <summary>
/// Marker interface for event-sourced aggregates. Carries the aggregate's id —
/// the stream id under which its events are persisted.
/// </summary>
/// <remarks>
/// Concrete aggregates implement this directly when they're tenant-agnostic
/// (e.g. <c>Customer</c>, <c>Tenant</c>) or implement <see cref="ITenantAggregate"/>
/// when they're owned by a tenant.
/// </remarks>
public interface IAggregate
{
    /// <summary>The aggregate id. Equal to its event-stream id.</summary>
    Guid Id { get; set; }
}
