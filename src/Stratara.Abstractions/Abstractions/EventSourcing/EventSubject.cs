namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Explicit Subject (data owner) for an event-source append. Used by
/// <see cref="IEventSource.AppendOnBehalfOfAsync{TAggregate}"/> when an event belongs to a
/// different owner than the one the store would resolve for it — the stream's recorded owner, a
/// creation event's tenant, or the tenant in the session.
/// </summary>
/// <param name="TenantId">The Subject tenant id.</param>
/// <param name="UserId">Optional Subject user id — <c>null</c> when the aggregate isn't user-scoped.</param>
public readonly record struct EventSubject(Guid TenantId, Guid? UserId = null);
