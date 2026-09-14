using Microsoft.Extensions.Logging;
using Stratara.Diagnostics;

namespace Stratara.Outbox.AzureServiceBus.Diagnostics.Extensions;

/// <summary>Source-generated logger extensions used by the Azure Service Bus message-bus implementation.</summary>
public static partial class LoggerAzureServiceBusExtensions
{
    /// <summary>Logs that a handler threw <c>ConcurrencyException</c> and the bus is abandoning the message for redelivery.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="streamId">The aggregate stream id involved in the conflict.</param>
    /// <param name="aggregateTypeName">The CLR type name of the aggregate.</param>
    [LoggerMessage(
        EventId = LogEvents.Messaging.ConcurrencyConflictRequeued,
        Level = LogLevel.Warning,
        Message = "Concurrency conflict on stream {StreamId} ({AggregateTypeName}) — abandoning for redelivery")]
    public static partial void LogConcurrencyConflictRequeued(this ILogger logger, Guid streamId, string aggregateTypeName);

    /// <summary>Logs that a message handler threw and the bus is dead-lettering the message.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="topic">The Service Bus topic that produced the failing message.</param>
    /// <param name="exception">The exception raised by the handler.</param>
    [LoggerMessage(
        EventId = LogEvents.Messaging.MessageProcessingFailed,
        Level = LogLevel.Error,
        Message = "Error processing message from topic {Topic}")]
    public static partial void LogMessageProcessingFailed(this ILogger logger, string topic, Exception exception);

    /// <summary>Logs that a message exhausted its redelivery bound and was moved to the subscription's dead-letter queue.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="topic">The topic the message was published to.</param>
    /// <param name="subscription">The subscription that could not take it.</param>
    /// <param name="reason"><c>conflict</c> or <c>failure</c>.</param>
    /// <param name="deliveryAttempt">How many times the message was delivered, counting the last.</param>
    [LoggerMessage(
        EventId = LogEvents.Messaging.MessageDeadLettered,
        Level = LogLevel.Warning,
        Message = "Message on topic {Topic} dead-lettered for subscription {Subscription} after {DeliveryAttempt} deliveries ({Reason}).")]
    public static partial void LogMessageDeadLettered(this ILogger logger, string topic, string subscription, string reason, int deliveryAttempt);

    /// <summary>Warns that the subscription's own delivery limit is below the framework's bounds, so the broker may dead-letter before the framework decides.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="topic">The topic.</param>
    /// <param name="subscription">The subscription whose limit is too low.</param>
    /// <param name="brokerLimit">The subscription's <c>MaxDeliveryCount</c>.</param>
    /// <param name="requiredLimit">The count the framework's bounds need.</param>
    [LoggerMessage(
        EventId = LogEvents.Messaging.BrokerDeliveryLimitBelowBounds,
        Level = LogLevel.Warning,
        Message = "Subscription {Subscription} on topic {Topic} has MaxDeliveryCount {BrokerLimit}, below the {RequiredLimit} the configured retry bounds need; the broker will dead-letter before the framework does.")]
    public static partial void LogBrokerDeliveryLimitBelowBounds(this ILogger logger, string topic, string subscription, int brokerLimit, int requiredLimit);
}
