using Microsoft.Extensions.Logging;
using Stratara.Diagnostics;

namespace Stratara.Shared.Diagnostics.Extensions;

/// <summary>
/// Source-generated structured-logging helpers for the command-handling worker lifecycle. Backed
/// by <see cref="LoggerMessageAttribute"/> with stable event ids from
/// <see cref="LogEvents.CommandProcessing"/>.
/// </summary>
public static partial class LoggerCommandExtensions
{
    /// <summary>Logs that the command-handling worker has started.</summary>
    /// <param name="logger">The logger to emit through.</param>
    [LoggerMessage(
        EventId = LogEvents.CommandProcessing.CommandWorkerStarted,
        Level = LogLevel.Information,
        Message = "Starting Command-Worker.")]
    public static partial void LogCommandWorkerStarted(this ILogger logger);

    /// <summary>Logs that the command-handling worker is shutting down.</summary>
    /// <param name="logger">The logger to emit through.</param>
    [LoggerMessage(
        EventId = LogEvents.CommandProcessing.CommandWorkerStopped,
        Level = LogLevel.Information,
        Message = "Stopping Command-Worker.")]
    public static partial void LogCommandWorkerStopped(this ILogger logger);

    /// <summary>Logs that a command-worker lane has bound to its topic and subscription.</summary>
    /// <param name="logger">The logger to emit through.</param>
    /// <param name="subscription">The subscription the lane consumes (interactive or heavy).</param>
    /// <param name="topic">The command topic the lane consumes.</param>
    /// <param name="degreeOfParallelism">The number of concurrent subscriptions the lane opens.</param>
    [LoggerMessage(
        EventId = LogEvents.CommandProcessing.CommandWorkerLaneStarted,
        Level = LogLevel.Information,
        Message = "Command-Worker lane bound to subscription {Subscription} on topic {Topic} with degree-of-parallelism {DegreeOfParallelism}.")]
    public static partial void LogCommandWorkerLaneStarted(this ILogger logger, string subscription, string topic, int degreeOfParallelism);

    /// <summary>Logs that a command envelope carried a signature that did not verify, and is still being dispatched under Permissive mode.</summary>
    /// <param name="logger">The logger to emit through.</param>
    /// <param name="envelopeId">The id of the offending envelope.</param>
    [LoggerMessage(
        EventId = LogEvents.CommandProcessing.CommandEnvelopeIntegrityWarning,
        Level = LogLevel.Warning,
        Message = "Command envelope {EnvelopeId} carries a signature that does not verify (Permissive mode) — dispatching anyway. The publisher holds a different key, or the envelope was altered in transit.")]
    public static partial void LogCommandEnvelopeIntegrityWarning(this ILogger logger, Guid envelopeId);

    /// <summary>Logs that a command envelope carried no signature, and is still being dispatched under Permissive mode.</summary>
    /// <param name="logger">The logger to emit through.</param>
    /// <param name="envelopeId">The id of the unsigned envelope.</param>
    [LoggerMessage(
        EventId = LogEvents.CommandProcessing.CommandEnvelopeUnsignedWarning,
        Level = LogLevel.Warning,
        Message = "Command envelope {EnvelopeId} carries no signature (Permissive mode) — dispatching anyway. Expected while publishers are still being rolled over to signing; afterwards it means a publisher was missed.")]
    public static partial void LogCommandEnvelopeUnsignedWarning(this ILogger logger, Guid envelopeId);

    /// <summary>Logs that a command envelope was rejected under Strict mode because its signature did not verify.</summary>
    /// <param name="logger">The logger to emit through.</param>
    /// <param name="envelopeId">The id of the rejected envelope.</param>
    [LoggerMessage(
        EventId = LogEvents.CommandProcessing.CommandEnvelopeIntegrityRejected,
        Level = LogLevel.Error,
        Message = "Command envelope {EnvelopeId} carries a signature that does not verify (Strict mode) — rejecting envelope.")]
    public static partial void LogCommandEnvelopeIntegrityRejected(this ILogger logger, Guid envelopeId);

    /// <summary>Logs that a command envelope was rejected under Strict mode because it carried no signature.</summary>
    /// <param name="logger">The logger to emit through.</param>
    /// <param name="envelopeId">The id of the rejected envelope.</param>
    [LoggerMessage(
        EventId = LogEvents.CommandProcessing.CommandEnvelopeUnsignedRejected,
        Level = LogLevel.Error,
        Message = "Command envelope {EnvelopeId} carries no signature (Strict mode) — rejecting envelope. A publisher is not signing.")]
    public static partial void LogCommandEnvelopeUnsignedRejected(this ILogger logger, Guid envelopeId);
}
