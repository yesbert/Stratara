using Microsoft.Extensions.Logging;
using Stratara.Diagnostics;

namespace Stratara.Shared.Diagnostics.Extensions;

/// <summary>
/// Source-generated <see cref="ILogger"/> extension methods for <see cref="Stratara.Outbox.RabbitMQ.Outbox.OutboxWorker"/>.
/// Event-IDs are defined in <c>LogEvents.OutboxProcessing</c>.
/// </summary>
public static partial class LoggerOutboxExtensions
{
    /// <summary>Logs that the outbox worker has started.</summary>
    /// <param name="logger">The logger.</param>
    [LoggerMessage(
        EventId = LogEvents.OutboxProcessing.OutboxWorkerStarted,
        Level = LogLevel.Information,
        Message = "Starting Outbox-Worker.")]
    public static partial void LogOutboxWorkerStarted(this ILogger logger);

    /// <summary>Logs that the outbox worker is shutting down.</summary>
    /// <param name="logger">The logger.</param>
    [LoggerMessage(
        EventId = LogEvents.OutboxProcessing.OutboxWorkerStopped,
        Level = LogLevel.Information,
        Message = "Stopping Outbox-Worker.")]
    public static partial void LogOutboxWorkerStopped(this ILogger logger);

    /// <summary>Logs that the outbox worker's processing loop was cancelled by the host.</summary>
    /// <param name="logger">The logger.</param>
    [LoggerMessage(
        EventId = LogEvents.OutboxProcessing.OutboxWorkerOperationCanceled,
        Level = LogLevel.Information,
        Message = "Outbox-Worker operation canceled.")]
    public static partial void LogOutboxWorkerOperationCanceled(this ILogger logger);

    /// <summary>Logs that the outbox drain attempt failed unexpectedly; the worker sleeps and retries.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The exception raised while draining the outbox.</param>
    [LoggerMessage(
        EventId = LogEvents.OutboxProcessing.OutboxWorkerFailed,
        Level = LogLevel.Error,
        Message = "Error while processing outbox.")]
    public static partial void LogOutboxFailed(this ILogger logger, Exception exception);

    /// <summary>Logs that another worker instance currently holds the outbox distributed lock; this cycle is skipped.</summary>
    /// <param name="logger">The logger.</param>
    [LoggerMessage(
        EventId = LogEvents.OutboxProcessing.OutboxLockNotAcquired,
        Level = LogLevel.Debug,
        Message = "Outbox distributed lock not acquired; another instance is draining. Skipping cycle.")]
    public static partial void LogOutboxLockNotAcquired(this ILogger logger);

    /// <summary>Logs that the distributed-lock store was unavailable when the outbox worker tried to acquire the lock.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The exception raised by the lock store.</param>
    [LoggerMessage(
        EventId = LogEvents.OutboxProcessing.OutboxLockUnavailable,
        Level = LogLevel.Warning,
        Message = "Outbox distributed lock store unavailable. Skipping cycle.")]
    public static partial void LogOutboxLockUnavailable(this ILogger logger, Exception exception);

    /// <summary>Logs that releasing the outbox distributed lock failed; the key will auto-expire on lease end.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The exception raised while releasing the lock.</param>
    [LoggerMessage(
        EventId = LogEvents.OutboxProcessing.OutboxLockReleaseFailed,
        Level = LogLevel.Warning,
        Message = "Failed to release outbox distributed lock. The lease will expire automatically.")]
    public static partial void LogOutboxLockReleaseFailed(this ILogger logger, Exception exception);

    /// <summary>
    /// Logs, once at start-up, that no shared coordination store is registered and replay
    /// coordination is therefore confined to this process.
    /// </summary>
    /// <param name="logger">The logger.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ProjectionReplayCoordinationInProcess,
        Level = LogLevel.Warning,
        Message = "No Redis connection is registered, so projection-replay coordination is confined to this process: a replay requested here suppresses publication here only. Call AddCaching() for a replay to span hosts.")]
    public static partial void LogProjectionReplayCoordinationInProcess(this ILogger logger);

    /// <summary>
    /// Logs that a subscriber to the in-process replay-request channel failed. The failure does not
    /// reach the caller of <c>RequestReplay</c> and the remaining subscribers are still notified.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The exception the subscriber raised.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ProjectionReplayRequestSubscriberFailed,
        Level = LogLevel.Warning,
        Message = "A projection-replay request subscriber failed. The other subscribers were notified.")]
    public static partial void LogProjectionReplayRequestSubscriberFailed(this ILogger logger, Exception exception);
}
