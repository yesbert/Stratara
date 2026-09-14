using Stratara.Contracts.Messages;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;

namespace Stratara.Abstractions.Outbox;

/// <summary>
/// Enqueues event bundles produced by the event-source on save for projection and
/// saga workers to consume. Implementations first try direct publish and fall back to
/// the outbox table if the bus is unreachable — or, where <see cref="StoresBundlesWithCommit"/>
/// says so, write the bundle to durable storage in the transaction that commits its events and
/// remove it once the bus has accepted it.
/// </summary>
public interface IEventBundleOutboxDispatcher
{
    /// <summary>
    /// Whether the event source must hand the bundle to <see cref="StoreEventBundleAsync"/> under
    /// the transaction that commits its events, before committing. <see langword="false"/> by
    /// default, which keeps the bus-first path: an implementation that does not override this
    /// never sees <see cref="StoreEventBundleAsync"/>.
    /// </summary>
    bool StoresBundlesWithCommit => false;

    /// <summary>
    /// Writes <paramref name="eventBundle"/> to durable storage under <paramref name="transaction"/>,
    /// so that it becomes durable in the same commit as the events it carries. Called by the event
    /// source only when <see cref="StoresBundlesWithCommit"/> is <see langword="true"/>; the
    /// subsequent <see cref="EnqueueEventBundleAsync"/> for the same bundle instance publishes it and
    /// removes the stored copy on acceptance.
    /// </summary>
    /// <param name="eventBundle">The bundle the save is about to commit.</param>
    /// <param name="transaction">The open write transaction the events are staged in.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="NotSupportedException">The implementation does not store bundles with the commit.</exception>
    Task StoreEventBundleAsync(EventBundle eventBundle, ITransaction transaction, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This dispatcher does not store bundles with the commit; it reports StoresBundlesWithCommit = false.");

    /// <summary>Enqueue <paramref name="eventBundle"/> for asynchronous dispatch.</summary>
    Task EnqueueEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drain previously-persisted <paramref name="outboxEntries"/> by attempting to
    /// publish each one and deleting on success. Used by the outbox worker.
    /// </summary>
    Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default);
}
