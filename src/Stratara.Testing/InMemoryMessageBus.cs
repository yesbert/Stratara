using System.Collections.Concurrent;
using Stratara.Abstractions.Messaging;

namespace Stratara.Testing;

/// <summary>
/// In-memory <see cref="IMessageBus"/> test double with synchronous, in-process dispatch: a
/// <see cref="PublishAsync{T}"/> call immediately awaits every handler subscribed to the topic
/// whose message type is compatible, so tests observe handler effects without a broker.
/// </summary>
/// <remarks>
/// Like a real broker, a message published to a topic with no current subscriber is dropped (but
/// still recorded in <see cref="Published"/> for assertions). Dispatch order follows subscription
/// order. All members are thread-safe.
/// </remarks>
/// <remarks>
/// A subscription established through <see cref="EnsureSubscriptionAsync"/> behaves differently, and
/// has to: the real broker retains for it from that moment, so a double that dropped would let a test
/// pass on start-up ordering that production fails. Messages published after a subscription is
/// established and before its handler attaches are held and delivered, in publish order, when it
/// attaches. Nothing is held for a subscription that was never established.
/// </remarks>
public sealed class InMemoryMessageBus : IMessageBus
{
    private readonly ConcurrentDictionary<string, List<Subscription>> _subscriptionsByTopic = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<PublishedMessage> _published = new();
    private readonly Dictionary<(string Topic, string Subscription), List<object>> _retained = [];
    private readonly HashSet<(string Topic, string Subscription)> _attached = [];
    private readonly Lock _gate = new();

    /// <summary>Every message handed to <see cref="PublishAsync{T}"/>, in publish order, for assertions.</summary>
    public IReadOnlyList<PublishedMessage> Published => _published.ToArray();

    /// <inheritdoc />
    public async Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(message);

        _published.Enqueue(new PublishedMessage(topic, message));

        Subscription[] handlers;
        lock (_gate)
        {
            handlers = _subscriptionsByTopic.TryGetValue(topic, out var list) ? [.. list] : [];

            foreach (var key in _retained.Keys.Where(k => k.Topic == topic && !_attached.Contains(k)).ToArray())
            {
                _retained[key].Add(message!);
            }
        }

        foreach (var subscription in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await subscription.InvokeAsync(message!).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task EnsureSubscriptionAsync(string topic, string subscription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(subscription);

        lock (_gate)
        {
            // Idempotent: re-establishing must not discard what is already held for it.
            if (!_retained.ContainsKey((topic, subscription)))
            {
                _retained[(topic, subscription)] = [];
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SubscribeAsync<T>(string topic, string subscription, Func<T, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(handler);

        Subscription entry;
        object[] held;
        lock (_gate)
        {
            entry = new Subscription(subscription, typeof(T), message => handler((T)message));
            var list = _subscriptionsByTopic.GetOrAdd(topic, _ => []);
            list.Add(entry);

            _attached.Add((topic, subscription));
            held = _retained.TryGetValue((topic, subscription), out var retained) ? [.. retained] : [];
            if (retained is not null)
            {
                retained.Clear();
            }
        }

        foreach (var message in held)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await entry.InvokeAsync(message).ConfigureAwait(false);
        }
    }

    private sealed record Subscription(string Name, Type MessageType, Func<object, Task> Handler)
    {
        public Task InvokeAsync(object message) =>
            MessageType.IsInstanceOfType(message) ? Handler(message) : Task.CompletedTask;
    }
}

/// <summary>A message captured by <see cref="InMemoryMessageBus"/> for test assertions.</summary>
/// <param name="Topic">The topic the message was published to.</param>
/// <param name="Message">The published message payload.</param>
public sealed record PublishedMessage(string Topic, object Message);
