using System.Collections.Concurrent;
using Stratara.Abstractions.Outbox;
using Stratara.Contracts.Messages;

namespace Stratara.Testing.EntityFrameworkCore;

/// <summary>
/// An <see cref="IEventBundleOutboxDispatcher"/> test double that records every dispatched bundle
/// instead of publishing to a broker. Lets tests assert which events the write side emitted on
/// <c>SaveChangesAsync</c> without standing up RabbitMQ or Azure Service Bus.
/// </summary>
public sealed class RecordingEventBundleOutboxDispatcher : IEventBundleOutboxDispatcher
{
    private readonly ConcurrentQueue<EventBundle> _bundles = new();

    /// <summary>Every bundle handed to <see cref="EnqueueEventBundleAsync"/>, in dispatch order.</summary>
    public IReadOnlyList<EventBundle> Bundles => _bundles.ToArray();

    /// <inheritdoc />
    public Task EnqueueEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventBundle);
        _bundles.Enqueue(eventBundle);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outboxEntries);
        return Task.CompletedTask;
    }
}
