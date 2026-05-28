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

    /// <summary>Logs that a command envelope failed integrity verification under Permissive mode and is still being dispatched.</summary>
    /// <param name="logger">The logger to emit through.</param>
    /// <param name="envelopeId">The id of the offending envelope.</param>
    [LoggerMessage(
        EventId = LogEvents.CommandProcessing.CommandEnvelopeIntegrityWarning,
        Level = LogLevel.Warning,
        Message = "Command envelope {EnvelopeId} integrity verification failed (Permissive mode) — dispatching but signature mismatch logged.")]
    public static partial void LogCommandEnvelopeIntegrityWarning(this ILogger logger, Guid envelopeId);

    /// <summary>Logs that a command envelope was rejected because integrity verification failed under Strict mode.</summary>
    /// <param name="logger">The logger to emit through.</param>
    /// <param name="envelopeId">The id of the rejected envelope.</param>
    [LoggerMessage(
        EventId = LogEvents.CommandProcessing.CommandEnvelopeIntegrityRejected,
        Level = LogLevel.Error,
        Message = "Command envelope {EnvelopeId} integrity verification failed (Strict mode) — rejecting envelope.")]
    public static partial void LogCommandEnvelopeIntegrityRejected(this ILogger logger, Guid envelopeId);
}
