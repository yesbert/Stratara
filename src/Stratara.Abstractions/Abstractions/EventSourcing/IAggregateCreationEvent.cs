namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Marker on the first event of an aggregate's lifecycle that states the owner of the new stream.
/// </summary>
/// <remarks>
/// The event's <see cref="TenantId"/> is recorded as the Subject (data-owner tenant) of the
/// stream's first event, and every later event on the stream keeps it, whoever's session appends.
/// This is where an aggregate created on behalf of another tenant states that tenant: without the
/// marker, a new stream takes the tenant in the session.
/// </remarks>
public interface IAggregateCreationEvent
{
    /// <summary>The Subject tenant id the new aggregate belongs to.</summary>
    Guid TenantId { get; }
}
