using System.Text.Json;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Messaging;
using Stratara.Diagnostics;
using Stratara.Outbox.AzureServiceBus.Diagnostics.Extensions;

namespace Stratara.Outbox.AzureServiceBus.Messaging;

/// <summary>
/// Azure Service Bus implementation of <see cref="IMessageBus"/>. Publishes JSON-serialized
/// messages to topics and exposes a subscription helper that wires up a Service Bus processor.
/// </summary>
/// <remarks>
/// <para>
/// Per-message handling classifies exceptions explicitly:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Success — message is completed (removed from the queue).
///   </description></item>
///   <item><description>
///     <see cref="ConcurrencyException"/> — message is abandoned so Service Bus redelivers it,
///     until <see cref="MessageRetryOptions.MaxConflictRequeues"/> redeliveries have not resolved
///     it; then it is dead-lettered with reason <c>conflict</c>.
///   </description></item>
///   <item><description>
///     Any other handler exception — message is abandoned for redelivery until
///     <see cref="MessageRetryOptions.MaxDeliveryAttempts"/> deliveries have failed; then it is
///     dead-lettered with reason <c>failure</c> and the exception in the description.
///   </description></item>
/// </list>
/// <para>
/// The decision is the framework's, read from the message's <c>DeliveryCount</c>, so the same
/// bounds mean the same number of handler runs as on RabbitMQ. The subscription's own
/// <c>MaxDeliveryCount</c> is a backstop and must be at least one above the larger bound — a
/// subscription created with Service Bus defaults allows 10 deliveries, well below the default
/// conflict bound of 100. Where an administration client is registered and the host may read the
/// subscription, a lower value is logged as a warning when the subscription is opened and the
/// bounds for that subscription are lowered to fit under it, so the framework still makes the move
/// and records it. Without that read the broker dead-letters first, under its own reason and
/// outside the framework's log and counter.
/// </para>
/// <para>
/// System-level errors (connection drops, auth failures) arrive via <c>ProcessErrorAsync</c> and
/// are logged but otherwise swallowed — the Service Bus client owns the reconnect / retry policy
/// for those.
/// </para>
/// <para>
/// A subscription that stops closes its processor, which takes no further message and waits for the handler it is
/// running to settle — as long as the host's shutdown timeout allows when the host is stopping, twenty seconds
/// otherwise. A handler still running after that keeps its message locked until the lock expires, and the client
/// closes the processor's links when it is disposed. A message taken after the subscription started stopping is
/// abandoned unhandled, and a handler that stops on the subscription's token has its message abandoned rather than
/// dead-lettered by the framework; the broker counts that delivery too.
/// </para>
/// </remarks>
/// <example>
/// Register Azure Service Bus as the host's <see cref="IMessageBus"/>:
/// <code>
/// builder.Services.AddAzureServiceBus(builder.Configuration.GetConnectionString("ServiceBus")!);
/// </code>
/// This registration <c>Replace</c>s the <see cref="IMessageBus"/> slot, so it wins even when the
/// RabbitMQ umbrella (<c>AddMessaging()</c>) already claimed it. Choose one transport per host.
/// </example>
internal sealed class AzureServiceBusBus(
    ILogger<AzureServiceBusBus> logger,
    ServiceBusClient client,
    IOptions<BusEnvelopeJsonOptions> envelopeOptions,
    IOptions<MessageRetryOptions> retryOptions,
    ServiceBusAdministrationClient? administration = null,
    AzureServiceBusSubscriptionStops? subscriptionStops = null) : IMessageBus, IAsyncDisposable
{
    private const int MaxDeadLetterDescriptionLength = 4096;

    /// <summary>
    /// How long a subscription that stops while the host is not stopping — its own token was cancelled — waits for the
    /// handler it is running to settle its message. While the host stops, the host's shutdown timeout bounds the wait
    /// instead (<see cref="AzureServiceBusSubscriptionStops"/>).
    /// </summary>
    private static readonly TimeSpan HandlerSettleTimeout = TimeSpan.FromSeconds(20);

    private readonly BusEnvelopeJsonOptions _envelopeOptions = envelopeOptions.Value;
    private readonly MessageRetryOptions _retryOptions = retryOptions.Value;
    private readonly MessageRetryPolicy _retryPolicy = new(retryOptions.Value);
    private readonly JsonSerializerOptions _deserializeOptions = BusEnvelopeJsonGuard.CreateOptions(envelopeOptions.Value.MaxDepth);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MessageRetryPolicy> _subscriptionPolicies = new(StringComparer.Ordinal);
    private readonly AzureServiceBusSubscriptionStops _stops = subscriptionStops ?? new(NullLogger<AzureServiceBusSubscriptionStops>.Instance);

    /// <inheritdoc/>
    /// <remarks>
    /// Waits for every subscription that was stopped to finish stopping, so its running handlers could settle. A host
    /// waits for them earlier, when it stops (<see cref="AzureServiceBusSubscriptionStops"/>); this covers a bus used
    /// without one.
    /// </remarks>
    public async ValueTask DisposeAsync() => await _stops.WhenAllStoppedAsync();

    /// <inheritdoc/>
    public async Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
    {
        var sender = client.CreateSender(topic);
        var json = JsonSerializer.Serialize(message);
        await sender.SendMessageAsync(new ServiceBusMessage(json), cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Does nothing, and that is correct rather than unimplemented. Service Bus topics and
    /// subscriptions are provisioned administratively — through the portal, a template or a
    /// deployment step — so a subscription exists before the application that reads it starts, and
    /// there is nothing for a host to establish. Do not "fix" this into a management-client call:
    /// the credentials a running host holds are data-plane credentials, and creating entities from
    /// them is a different permission and a different lifecycle.
    /// </remarks>
    public Task EnsureSubscriptionAsync(string topic, string subscription, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc/>
    public async Task SubscribeAsync<T>(string topic, string subscription, Func<T, Task> handler, CancellationToken cancellationToken = default)
    {
        var retryPolicy = await ResolveRetryPolicyAsync(topic, subscription, cancellationToken);

        var processor = client.CreateProcessor(topic, subscription, new ServiceBusProcessorOptions());

        processor.ProcessMessageAsync += async args =>
        {
            // A message the processor took after the subscription started stopping — before the processor itself has
            // stopped — is not handled: it goes back for the next consumer instead of running with a cancelled token.
            if (cancellationToken.IsCancellationRequested)
            {
                await args.AbandonMessageAsync(args.Message, cancellationToken: CancellationToken.None);
                return;
            }

            try
            {
                var body = args.Message.Body.ToMemory();
                BusEnvelopeJsonGuard.EnsureWithinSizeLimit(body.Length, _envelopeOptions.MaxBodyBytes, topic);
                var message = JsonSerializer.Deserialize<T>(body.Span, _deserializeOptions);
                if (message is not null)
                {
                    await handler(message);
                }

                // Settled whatever the subscription's token says: a handler that completed while the subscription
                // stops has done its work, and a completion that throws would deliver the message again.
                await args.CompleteMessageAsync(args.Message, CancellationToken.None);
            }
            catch (CommittedEventsNotPublishedException committed)
            {
                // Settled whatever the subscription's token says: a stopping subscription must not abandon it, which
                // would deliver the message again.
                logger.LogCommittedEventsNotPublished(topic, committed);
                await args.CompleteMessageAsync(args.Message, CancellationToken.None);
            }
            catch (ConcurrencyException ce)
            {
                // Settled whatever the subscription's token says, like every outcome: a settlement that throws would
                // leave the decision to the broker.
                await SettleFailedAsync(args, topic, subscription, retryPolicy, MessageFailureKind.Conflict, ce, CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The handler stopped because the subscription stops — the host is shutting down. That is not a
                // failure: the framework abandons the message for the next consumer rather than dead-letter it. The broker
                // counts the delivery like any other.
                await args.AbandonMessageAsync(args.Message, cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogMessageProcessingFailed(topic, ex);
                await SettleFailedAsync(args, topic, subscription, retryPolicy, MessageFailureKind.Failure, ex, CancellationToken.None);
            }
        };

        processor.ProcessErrorAsync += errorArgs =>
        {
            logger.LogMessageProcessingFailed(topic, errorArgs.Exception);
            return Task.CompletedTask;
        };

        try
        {
            await processor.StartProcessingAsync(cancellationToken);
        }
        catch
        {
            // Nothing processes yet, and nothing would close the processor later.
            await processor.DisposeAsync();
            throw;
        }

        // A stopping subscription closes its processor, which takes no further message and waits for the handlers it is
        // running: a handler that completed — or whose save committed — while the host stops settles its message instead
        // of having it delivered again.
        cancellationToken.Register(() => _stops.Add(StopAsync()));

        async Task StopAsync()
        {
            // Taken when the token is cancelled, so it is the host's shutdown timeout while the host stops.
            using var settle = _stops.Deadline(HandlerSettleTimeout);
            await Task.Yield();

            // Closing is what disposing the processor does, and it is never cancelled itself: a close cancelled before it
            // began would leave the processor receiving. Only the wait for it is bounded — a handler that does not settle
            // in time keeps its message locked until the lock expires, the close finishes in the background, and the
            // client closes the links when it is disposed. An unbounded wait here would hold the host's stop, and its
            // disposal, up for as long as the handler runs.
            var closing = processor.CloseAsync(CancellationToken.None);
            try
            {
                await closing.WaitAsync(settle.Token);
            }
            catch (Exception ex)
            {
                logger.LogSubscriptionCleanupFailed(subscription, ex);
                if (!closing.IsCompleted)
                {
                    _ = closing.ContinueWith(
                        closed => logger.LogSubscriptionCleanupFailed(subscription, closed.Exception!),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
        }
    }

    private async Task SettleFailedAsync(ProcessMessageEventArgs args, string topic, string subscription, MessageRetryPolicy retryPolicy, MessageFailureKind kind, Exception cause, CancellationToken cancellationToken)
    {
        var attempt = args.Message.DeliveryCount;
        if (retryPolicy.Decide(attempt, kind) == MessageDisposition.Redeliver)
        {
            await args.AbandonMessageAsync(args.Message, cancellationToken: cancellationToken);
            if (cause is ConcurrencyException conflict)
            {
                logger.LogConcurrencyConflictRequeued(conflict.StreamId, conflict.AggregateTypeName);
            }

            return;
        }

        var reason = MessageRetryPolicy.ReasonFor(kind);
        var description = $"{cause.GetType().Name}: {cause.Message}";
        if (description.Length > MaxDeadLetterDescriptionLength)
        {
            description = description[..MaxDeadLetterDescriptionLength];
        }

        await args.DeadLetterMessageAsync(args.Message, reason, description, cancellationToken);
        logger.LogMessageDeadLettered(topic, subscription, reason, attempt);
        ApplicationDiagnostics.Metrics.MessagesDeadLettered.Add(1,
            new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.Topic, topic),
            new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.Subscription, subscription),
            new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.Reason, reason));
    }

    /// <summary>
    /// The bounds that apply on one subscription. The subscription's <c>MaxDeliveryCount</c> is read
    /// once; where it is below what the configured bounds need, the broker would dead-letter first,
    /// under its own reason and outside the framework's log and counter, so a warning is logged and
    /// the bounds are lowered to fit under the broker's limit. Advisory: a host may hold the right to
    /// read messages without the right to read the subscription's definition, so a failed read ends
    /// the check rather than the subscription, and the configured bounds apply unchanged.
    /// </summary>
    internal async Task<MessageRetryPolicy> ResolveRetryPolicyAsync(string topic, string subscription, CancellationToken cancellationToken)
    {
        if (administration is null)
        {
            return _retryPolicy;
        }

        var key = $"{topic}/{subscription}";
        if (_subscriptionPolicies.TryGetValue(key, out var known))
        {
            return known;
        }

        var policy = _retryPolicy;
        try
        {
            var properties = await administration.GetSubscriptionAsync(topic, subscription, cancellationToken);
            var brokerLimit = properties.Value.MaxDeliveryCount;
            if (brokerLimit < _retryPolicy.BrokerDeliveryLimit)
            {
                logger.LogBrokerDeliveryLimitBelowBounds(topic, subscription, brokerLimit, _retryPolicy.BrokerDeliveryLimit);
                policy = RetryPolicyWithin(_retryOptions, brokerLimit);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Advisory, see the summary: no read, no adjustment. The configured bounds apply.
        }

        return _subscriptionPolicies.GetOrAdd(key, policy);
    }

    /// <summary>
    /// The configured bounds, lowered so that the framework decides no later than the delivery the
    /// broker's own limit would dead-letter after: a failure on that delivery at the latest, a
    /// conflict on it as well, which is one requeue fewer than the limit.
    /// </summary>
    internal static MessageRetryPolicy RetryPolicyWithin(MessageRetryOptions options, int brokerLimit) =>
        new(new MessageRetryOptions
        {
            MaxDeliveryAttempts = Math.Max(1, Math.Min(options.MaxDeliveryAttempts, brokerLimit)),
            MaxConflictRequeues = Math.Max(0, Math.Min(options.MaxConflictRequeues, brokerLimit - 1)),
        });
}