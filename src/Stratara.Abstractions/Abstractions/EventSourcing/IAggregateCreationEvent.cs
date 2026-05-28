namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Marker on the first event of an aggregate's lifecycle. The EventSource uses this to
/// resolve the Subject (data-owner tenant id) when a stream is being created and no
/// existing aggregate / per-batch cache entry is available yet.
/// </summary>
public interface IAggregateCreationEvent
{
    /// <summary>The Subject tenant id the new aggregate belongs to.</summary>
    Guid TenantId { get; }
}
