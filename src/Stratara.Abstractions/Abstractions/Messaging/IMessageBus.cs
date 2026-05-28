namespace Stratara.Abstractions.Messaging;

/// <summary>
/// Provider-agnostic pub/sub abstraction over the underlying message bus (RabbitMQ in
/// dev, Azure Service Bus in prod). Topic + subscription names follow
/// <see cref="IMessagingIdentifier"/>.
/// </summary>
public interface IMessageBus
{
    /// <summary>Publish <paramref name="message"/> to <paramref name="topic"/>.</summary>
    Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribe to <paramref name="topic"/> under <paramref name="subscription"/> and
    /// dispatch every incoming message to <paramref name="handler"/>.
    /// </summary>
    Task SubscribeAsync<T>(string topic, string subscription, Func<T, Task> handler, CancellationToken cancellationToken = default);
}
