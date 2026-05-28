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
}
