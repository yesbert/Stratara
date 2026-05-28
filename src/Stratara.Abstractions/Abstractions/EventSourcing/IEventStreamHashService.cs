namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Computes per-entry hashes that link events in a stream into a tamper-evident chain.
/// Driven by the <c>EventStreamHashing</c> worker; runs in batches over the
/// not-yet-hashed tail of the event store.
/// </summary>
public interface IEventStreamHashService
{
    /// <summary>
    /// Process the next batch of unhashed events. Stops cleanly when there is nothing
    /// to do or when <paramref name="stoppingToken"/> is signalled.
    /// </summary>
    Task HashEventsAsync(CancellationToken stoppingToken = default);
}
