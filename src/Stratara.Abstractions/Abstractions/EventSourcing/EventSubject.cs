namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Explicit Subject (data owner) for an event-source append. Used by
/// <see cref="IEventSource.AppendOnBehalfOfAsync{TAggregate}"/> when the Subject is
/// intentionally different from the calling SessionContext's Subject (PlatformAdmin
/// cross-tenant flows, EventStoreMigration regeneration).
/// </summary>
/// <param name="TenantId">The Subject tenant id.</param>
/// <param name="UserId">Optional Subject user id — <c>null</c> when the aggregate isn't user-scoped.</param>
public readonly record struct EventSubject(Guid TenantId, Guid? UserId = null);
