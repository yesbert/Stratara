using System.Collections.Concurrent;
using System.Threading.Channels;
using Stratara.Abstractions.Messaging;

namespace Stratara.Sample.OutboxWorker.Messaging;

public sealed class InMemoryMessageBus : IMessageBus
{
    private readonly ConcurrentDictionary<string, Channel<object>> _topics = new();

    public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
    {
        var channel = _topics.GetOrAdd(topic, _ => Channel.CreateUnbounded<object>());
        return channel.Writer.WriteAsync(message!, cancellationToken).AsTask();
    }

    public Task EnsureSubscriptionAsync(string topic, string subscription, CancellationToken cancellationToken = default)
    {
        // Creating the channel is what starts retention here: it is unbounded, so everything
        // published from now on waits for whoever reads it.
        _topics.GetOrAdd(topic, _ => Channel.CreateUnbounded<object>());
        return Task.CompletedTask;
    }

    public async Task SubscribeAsync<T>(string topic, string subscription, Func<T, Task> handler,
        CancellationToken cancellationToken = default)
    {
        var channel = _topics.GetOrAdd(topic, _ => Channel.CreateUnbounded<object>());
        await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (message is T typed)
            {
                await handler(typed).ConfigureAwait(false);
            }
        }
    }
}
