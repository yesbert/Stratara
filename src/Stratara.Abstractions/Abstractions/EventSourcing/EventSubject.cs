namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Explicit Subject (data owner) for an event-source append. Used by
/// <see cref="IEventSource.AppendOnBehalfOfAsync{TAggregate}"/> when an event belongs to a
/// different owner than the one the store would resolve for it — the stream's recorded owner, a
/// creation event's tenant, or the tenant in the session.
/// </summary>
/// <remarks>
/// A Subject names a tenant, optionally with a user, or a user alone. A user alone owns data that
/// belongs to a person rather than to any of their tenants — a profile that follows them across
/// tenants: it is recorded under the reserved default tenant (<see cref="Guid.Empty"/>), its encrypted
/// fields are keyed to the user, the user's erasure shreds them, and no tenant's erasure reaches them.
/// A stream created for a user alone keeps that owner for every later event, whatever tenant the
/// appending session names.
/// </remarks>
/// <param name="TenantId">The Subject tenant id; <see cref="Guid.Empty"/> for data owned by a user alone.</param>
/// <param name="UserId">Optional Subject user id — <c>null</c> when the aggregate isn't user-scoped.</param>
public readonly record struct EventSubject(Guid TenantId, Guid? UserId = null)
{
    /// <summary>Whether the Subject names an owner at all: a tenant, or a user.</summary>
    public bool NamesAnOwner => TenantId != Guid.Empty || UserId is { } user && user != Guid.Empty;

    /// <summary>A Subject for data owned by <paramref name="userId"/> alone, under no tenant of theirs.</summary>
    /// <param name="userId">The user who owns the data.</param>
    /// <returns>The Subject, recorded under the reserved default tenant.</returns>
    /// <exception cref="ArgumentException"><paramref name="userId"/> is empty.</exception>
    public static EventSubject ForUser(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user-owned Subject needs a user id.", nameof(userId));
        }

        return new EventSubject(Guid.Empty, userId);
    }
}
