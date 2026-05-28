using Stratara.Contracts.Messages;
using Stratara.Abstractions.Outbox;

namespace Stratara.Abstractions.Outbox;

/// <summary>
/// Enqueues event bundles produced by the event-source on save for projection and
/// saga workers to consume. Implementations first try direct publish and fall back to
/// the outbox table if the bus is unreachable.
/// </summary>
public interface IEventBundleOutboxDispatcher
{
    /// <summary>Enqueue <paramref name="eventBundle"/> for asynchronous dispatch.</summary>
    Task EnqueueEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drain previously-persisted <paramref name="outboxEntries"/> by attempting to
    /// publish each one and deleting on success. Used by the outbox worker.
    /// </summary>
    Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default);
}
