using Microsoft.Extensions.Logging;
using Stratara.Diagnostics;

namespace Stratara.Shared.Diagnostics.Extensions;

/// <summary>
/// Source-generated <see cref="ILogger"/> extension methods for the Stratara messaging stack
/// (RabbitMQ / Azure Service Bus dispatch and consumption). Event-IDs are defined in
/// <c>LogEvents.Messaging</c>.
/// </summary>
public static partial class LoggerMessagingExtensions
{
    /// <summary>Logs that a message could not be processed by a subscription handler.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="topic">The topic / queue / exchange the message originated from.</param>
    /// <param name="exception">The exception raised by the handler or by the deserializer.</param>
    [LoggerMessage(
        EventId = LogEvents.Messaging.MessageProcessingFailed,
        Level = LogLevel.Error,
        Message = "Error processing message from topic {Topic}")]
    public static partial void LogMessageProcessingFailed(this ILogger logger, string topic, Exception exception);

    /// <summary>Logs that a message body could not be deserialized into the expected payload type.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="topic">The topic / queue / exchange the message originated from.</param>
    /// <param name="exception">The deserialization exception.</param>
    [LoggerMessage(
        EventId = LogEvents.Messaging.MessageDeserializationFailed,
        Level = LogLevel.Error,
        Message = "Error deserialize message from topic {Topic}")]
    public static partial void LogMessageDeserializationFailed(this ILogger logger, string topic, Exception exception);

    /// <summary>
    /// Logs that a direct publish of a <c>CommandEnvelope</c> failed. The dispatcher will fall back
    /// to storing the envelope in the outbox table and rely on <see cref="Stratara.Outbox.RabbitMQ.Outbox.OutboxWorker"/>
    /// to retry the publish.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="topic">The command topic that failed.</param>
    /// <param name="exception">The exception raised by the message-bus client.</param>
    [LoggerMessage(
        EventId = LogEvents.Messaging.CommandEnvelopeDispatchFailed,
        Level = LogLevel.Warning,
        Message = "Error publishing command to topic {Topic}. Will store in outbox.")]
    public static partial void LogCommandEnvelopeDispatchFailed(this ILogger logger, string topic, Exception exception);

    /// <summary>
    /// Logs that a direct publish of an <c>EventBundle</c> failed. The dispatcher will fall back
    /// to storing the bundle in the outbox table and rely on <see cref="Stratara.Outbox.RabbitMQ.Outbox.OutboxWorker"/>
    /// to retry the publish.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="topic">The event-bundle topic that failed.</param>
    /// <param name="exception">The exception raised by the message-bus client.</param>
    [LoggerMessage(
        EventId = LogEvents.Messaging.EventBundleDispatchFailed,
        Level = LogLevel.Warning,
        Message = "Error publishing event bundle to topic {Topic}. Will store in outbox.")]
    public static partial void LogEventBundleDispatchFailed(this ILogger logger, string topic, Exception exception);

    /// <summary>
    /// Logs that a write-side concurrency conflict was detected while applying a command;
    /// the message has been NACKed with <c>requeue=true</c> for a retry against the new aggregate version.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="streamId">The aggregate stream that conflicted.</param>
    /// <param name="aggregateTypeName">CLR type name of the aggregate.</param>
    [LoggerMessage(
        EventId = LogEvents.Messaging.ConcurrencyConflictRequeued,
        Level = LogLevel.Information,
        Message = "Concurrency conflict on stream {StreamId} ({AggregateTypeName}); message requeued for retry.")]
    public static partial void LogConcurrencyConflictRequeued(this ILogger logger, Guid streamId, string aggregateTypeName);

    /// <summary>Logs that the RabbitMQ subscription is shutting down (cancellation requested).</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="subscription">Identifier of the subscription being cleaned up.</param>
    [LoggerMessage(
        EventId = LogEvents.Messaging.SubscriptionCleanup,
        Level = LogLevel.Information,
        Message = "Cleaning up RabbitMQ subscription {Subscription}.")]
    public static partial void LogSubscriptionCleanup(this ILogger logger, string subscription);

    /// <summary>Logs that the RabbitMQ subscription cleanup did not succeed cleanly.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="subscription">Identifier of the subscription being cleaned up.</param>
    /// <param name="exception">The cleanup exception.</param>
    [LoggerMessage(
        EventId = LogEvents.Messaging.SubscriptionCleanupFailed,
        Level = LogLevel.Warning,
        Message = "Error during RabbitMQ subscription cleanup for {Subscription}.")]
    public static partial void LogSubscriptionCleanupFailed(this ILogger logger, string subscription, Exception exception);

    /// <summary>Warns that the bus is falling back to the default RabbitMQ guest/guest credentials.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="host">The configured RabbitMQ host.</param>
    [LoggerMessage(
        EventId = LogEvents.Messaging.RabbitMqGuestFallback,
        Level = LogLevel.Warning,
        Message = "RABBITMQ_USERNAME / RABBITMQ_PASSWORD not set — falling back to default guest credentials for host {Host}. Production hosts must set both.")]
    public static partial void LogRabbitMqGuestFallback(this ILogger logger, string host);

    /// <summary>Logs that disposing the publish channel / connection before a recreate failed; the recreate proceeds anyway.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The cleanup exception.</param>
    [LoggerMessage(
        EventId = LogEvents.Messaging.PublishChannelCleanupFailed,
        Level = LogLevel.Warning,
        Message = "Error disposing RabbitMQ publish channel / connection before re-create; continuing.")]
    public static partial void LogPublishChannelCleanupFailed(this ILogger logger, Exception exception);
}
