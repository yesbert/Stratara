using System.Collections.Concurrent;
using System.Threading.Channels;
using Stratara.Abstractions.Messaging;

namespace Stratara.Sample.MoneyTransferSaga.Messaging;

public sealed class InMemoryMessageBus : IMessageBus
{
    private readonly ConcurrentDictionary<string, Channel<object>> _topics = new();

    public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
    {
        var channel = _topics.GetOrAdd(topic, _ => Channel.CreateUnbounded<object>());
        return channel.Writer.WriteAsync(message!, cancellationToken).AsTask();
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
