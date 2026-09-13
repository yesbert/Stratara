using System.Text.Json;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
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
/// <c>MaxDeliveryCount</c> is a backstop and must be at least one above the larger bound; where an
/// administration client is registered and the host may read the subscription, a lower value is
/// logged as a warning when the subscription is opened.
/// </para>
/// <para>
/// System-level errors (connection drops, auth failures) arrive via <c>ProcessErrorAsync</c> and
/// are logged but otherwise swallowed — the Service Bus client owns the reconnect / retry policy
/// for those.
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
    ServiceBusAdministrationClient? administration = null) : IMessageBus
{
    private const int MaxDeadLetterDescriptionLength = 4096;

    private readonly BusEnvelopeJsonOptions _envelopeOptions = envelopeOptions.Value;
    private readonly MessageRetryPolicy _retryPolicy = new(retryOptions.Value);
    private readonly JsonSerializerOptions _deserializeOptions = BusEnvelopeJsonGuard.CreateOptions(envelopeOptions.Value.MaxDepth);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _checkedSubscriptions = new(StringComparer.Ordinal);

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
        await WarnIfBrokerLimitIsBelowBoundsAsync(topic, subscription, cancellationToken);

        var processor = client.CreateProcessor(topic, subscription, new ServiceBusProcessorOptions());

        processor.ProcessMessageAsync += async args =>
        {
            try
            {
                var body = args.Message.Body.ToMemory();
                BusEnvelopeJsonGuard.EnsureWithinSizeLimit(body.Length, _envelopeOptions.MaxBodyBytes, topic);
                var message = JsonSerializer.Deserialize<T>(body.Span, _deserializeOptions);
                if (message is not null)
                {
                    await handler(message);
                }

                await args.CompleteMessageAsync(args.Message, cancellationToken);
            }
            catch (ConcurrencyException ce)
            {
                logger.LogConcurrencyConflictRequeued(ce.StreamId, ce.AggregateTypeName);
                await SettleFailedAsync(args, topic, subscription, MessageFailureKind.Conflict, ce, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogMessageProcessingFailed(topic, ex);
                await SettleFailedAsync(args, topic, subscription, MessageFailureKind.Failure, ex, cancellationToken);
            }
        };

        processor.ProcessErrorAsync += errorArgs =>
        {
            logger.LogMessageProcessingFailed(topic, errorArgs.Exception);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(cancellationToken);
    }

    private async Task SettleFailedAsync(ProcessMessageEventArgs args, string topic, string subscription, MessageFailureKind kind, Exception cause, CancellationToken cancellationToken)
    {
        var attempt = args.Message.DeliveryCount;
        if (_retryPolicy.Decide(attempt, kind) == MessageDisposition.Redeliver)
        {
            await args.AbandonMessageAsync(args.Message, cancellationToken: cancellationToken);
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
    /// Reads the subscription's <c>MaxDeliveryCount</c> once per subscription and warns when it is
    /// below what the bounds need. Advisory: a host may hold the right to read messages without the
    /// right to read the subscription's definition, and the bounds apply either way, so a refusal
    /// or a management endpoint that is not there ends the check rather than the subscription.
    /// </summary>
    private async Task WarnIfBrokerLimitIsBelowBoundsAsync(string topic, string subscription, CancellationToken cancellationToken)
    {
        if (administration is null || !_checkedSubscriptions.TryAdd($"{topic}/{subscription}", true))
        {
            return;
        }

        try
        {
            var properties = await administration.GetSubscriptionAsync(topic, subscription, cancellationToken);
            if (properties.Value.MaxDeliveryCount < _retryPolicy.BrokerDeliveryLimit)
            {
                logger.LogBrokerDeliveryLimitBelowBounds(topic, subscription, properties.Value.MaxDeliveryCount, _retryPolicy.BrokerDeliveryLimit);
            }
        }
        catch (Exception ex) when (ex is RequestFailedException or ServiceBusException or UnauthorizedAccessException)
        {
        }
    }
}