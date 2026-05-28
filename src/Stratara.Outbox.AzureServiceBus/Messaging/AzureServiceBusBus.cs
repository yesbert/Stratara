using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Messaging;
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
///     <see cref="ConcurrencyException"/> — message is abandoned so Service Bus redelivers it.
///     This mirrors the RabbitMQ NACK-requeue path: the next delivery sees the updated aggregate
///     version and retries from a clean baseline.
///   </description></item>
///   <item><description>
///     Any other handler exception — message is dead-lettered with the exception type as the
///     dead-letter reason. Without explicit dead-lettering, Service Bus would silently retry up
///     to <c>MaxDeliveryCount</c> times before auto-DLQ; calling it explicitly surfaces the
///     poison message immediately and records the cause.
///   </description></item>
/// </list>
/// <para>
/// System-level errors (connection drops, auth failures) arrive via <c>ProcessErrorAsync</c> and
/// are logged but otherwise swallowed — the Service Bus client owns the reconnect / retry policy
/// for those.
/// </para>
/// </remarks>
/// <example>
/// Replace the default <see cref="IMessageBus"/> registration (RabbitMQ) with Azure Service Bus:
/// <code>
/// builder.Services
///     .AddSingleton(_ =&gt; new ServiceBusClient(builder.Configuration.GetConnectionString("ServiceBus")!))
///     .AddSingleton&lt;IMessageBus, AzureServiceBusBus&gt;();
/// </code>
/// </example>
internal sealed class AzureServiceBusBus(ILogger<AzureServiceBusBus> logger, ServiceBusClient client, IOptions<BusEnvelopeJsonOptions> envelopeOptions) : IMessageBus
{
    private readonly BusEnvelopeJsonOptions _envelopeOptions = envelopeOptions.Value;
    private readonly JsonSerializerOptions _deserializeOptions = BusEnvelopeJsonGuard.CreateOptions(envelopeOptions.Value.MaxDepth);

    /// <inheritdoc/>
    public async Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
    {
        var sender = client.CreateSender(topic);
        var json = JsonSerializer.Serialize(message);
        await sender.SendMessageAsync(new ServiceBusMessage(json), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task SubscribeAsync<T>(string topic, string subscription, Func<T, Task> handler, CancellationToken cancellationToken = default)
    {
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
                await args.AbandonMessageAsync(args.Message, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogMessageProcessingFailed(topic, ex);
                await args.DeadLetterMessageAsync(args.Message, ex.GetType().Name, ex.Message, cancellationToken);
            }
        };

        processor.ProcessErrorAsync += errorArgs =>
        {
            logger.LogMessageProcessingFailed(topic, errorArgs.Exception);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(cancellationToken);
    }
}