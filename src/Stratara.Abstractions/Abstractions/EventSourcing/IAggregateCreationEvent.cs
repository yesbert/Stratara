namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Marker on the first event of an aggregate's lifecycle that states the owner of the new stream.
/// </summary>
/// <remarks>
/// A non-empty <see cref="TenantId"/> is recorded as the Subject (data-owner tenant) of the
/// stream's first event, and every later event on the stream keeps it, whatever session appends
/// to it. An aggregate created for another tenant than the session's states that tenant here:
/// without the marker, or with an empty tenant, a new stream takes the tenant in the session.
/// </remarks>
public interface IAggregateCreationEvent
{
    /// <summary>The Subject tenant id the new aggregate belongs to.</summary>
    Guid TenantId { get; }
}
