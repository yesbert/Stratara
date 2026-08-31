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
    /// Establish <paramref name="subscription"/> on <paramref name="topic"/> so that messages
    /// published from this point on are retained for it, without dispatching any of them yet.
    /// </summary>
    /// <param name="topic">The topic the subscription belongs to.</param>
    /// <param name="subscription">The subscription to establish.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once the subscription exists.</returns>
    /// <remarks>
    /// <para>
    /// Call this during start-up, before anything in the system can publish. A subscription that is
    /// established only when its handler attaches receives nothing published in the meantime, and on
    /// a topic carrying more than one subscription that loss is silent: the publisher is told the
    /// publication succeeded as soon as <em>any</em> subscription takes it, so a missing one is
    /// indistinguishable from a delivered one.
    /// </para>
    /// <para>
    /// Idempotent — establishing a subscription that already exists changes nothing and loses
    /// nothing already held for it. <see cref="SubscribeAsync{T}"/> establishes the subscription too,
    /// so a caller that only ever subscribes stays correct; this member exists so that establishing
    /// can happen earlier than the handler is ready.
    /// </para>
    /// <para>
    /// An implementation whose subscriptions exist before the application runs — one where they are
    /// provisioned administratively — satisfies this by doing nothing. An implementation that cannot
    /// establish a particular subscription ahead of its consumer SHOULD throw rather than return
    /// successfully, because a caller cannot otherwise tell that the guarantee it asked for is absent.
    /// </para>
    /// </remarks>
    Task EnsureSubscriptionAsync(string topic, string subscription, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribe to <paramref name="topic"/> under <paramref name="subscription"/> and
    /// dispatch every incoming message to <paramref name="handler"/>. Establishes the subscription
    /// if <see cref="EnsureSubscriptionAsync"/> has not already done so.
    /// </summary>
    Task SubscribeAsync<T>(string topic, string subscription, Func<T, Task> handler, CancellationToken cancellationToken = default);
}
