using Microsoft.Extensions.Logging;
using Stratara.Diagnostics;

namespace Stratara.Shared.Diagnostics.Extensions;

/// <summary>
/// Source-generated structured-logging helpers for the event-stream hashing worker lifecycle and
/// failure modes. Backed by <see cref="LoggerMessageAttribute"/> with stable event ids from
/// <see cref="LogEvents.EventStreamHashing"/>.
/// </summary>
public static partial class LoggerEventStreamHashingExtensions
{
    /// <summary>Logs that the event-stream-hashing worker has started.</summary>
    /// <param name="logger">The logger to emit through.</param>
    [LoggerMessage(
        EventId = LogEvents.EventStreamHashing.EventStreamHashWorkerStarted,
        Level = LogLevel.Information,
        Message = "Starting EventStreamHash-Worker.")]
    public static partial void LogEventStreamHashWorkerStarted(this ILogger logger);

    /// <summary>Logs that the event-stream-hashing worker is shutting down.</summary>
    /// <param name="logger">The logger to emit through.</param>
    [LoggerMessage(
        EventId = LogEvents.EventStreamHashing.EventStreamHashWorkerStopped,
        Level = LogLevel.Information,
        Message = "Stopping EventStreamHash-Worker.")]
    public static partial void LogEventStreamHashWorkerStopped(this ILogger logger);

    /// <summary>Logs that the event-stream-hashing worker was cooperatively canceled.</summary>
    /// <param name="logger">The logger to emit through.</param>
    [LoggerMessage(
        EventId = LogEvents.EventStreamHashing.EventStreamHashWorkerOperationCanceled,
        Level = LogLevel.Information,
        Message = "EventStreamHash-Worker operation canceled.")]
    public static partial void LogEventStreamHashWorkerOperationCanceled(this ILogger logger);

    /// <summary>Logs that the event-stream-hashing worker threw an unhandled exception while hashing.</summary>
    /// <param name="logger">The logger to emit through.</param>
    /// <param name="exception">The exception thrown by the worker.</param>
    [LoggerMessage(
        EventId = LogEvents.EventStreamHashing.EventStreamHashWorkerFailed,
        Level = LogLevel.Error,
        Message = "Error while hashing event stream.")]
    public static partial void LogEventStreamHashWorkerFailed(this ILogger logger, Exception exception);
}
