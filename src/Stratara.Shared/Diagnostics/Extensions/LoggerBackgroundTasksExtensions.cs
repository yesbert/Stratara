using Microsoft.Extensions.Logging;
using Stratara.Diagnostics;

namespace Stratara.Shared.Diagnostics.Extensions;

/// <summary>
/// Source-generated structured-logging helpers for background-task / hosted-service lifecycle and
/// per-job execution outcomes. Backed by <see cref="LoggerMessageAttribute"/> with stable event ids
/// from <see cref="LogEvents.BackgroundTasks"/>.
/// </summary>
public static partial class LoggerBackgroundTasksExtensions
{
    /// <summary>Logs that the queued hosted service has started.</summary>
    /// <param name="logger">The logger to emit through.</param>
    [LoggerMessage(
        EventId = LogEvents.BackgroundTasks.QueuedHostedServiceStarted,
        Level = LogLevel.Information,
        Message = "Queued Hosted Service is running.")]
    public static partial void LogQueuedHostedServiceStarted(this ILogger logger);

    /// <summary>Logs that the queued hosted service is shutting down.</summary>
    /// <param name="logger">The logger to emit through.</param>
    [LoggerMessage(
        EventId = LogEvents.BackgroundTasks.QueuedHostedServiceStopped,
        Level = LogLevel.Information,
        Message = "Queued Hosted Service is stopping.")]
    public static partial void LogQueuedHostedServiceStopped(this ILogger logger);

    /// <summary>Logs that a background job of a given CLR type executed without error.</summary>
    /// <param name="logger">The logger to emit through.</param>
    /// <param name="jobTypeName">CLR type name of the job class.</param>
    [LoggerMessage(
        EventId = LogEvents.BackgroundTasks.JobSuccessfulExecuted,
        Level = LogLevel.Debug,
        Message = "Job of type {JobTypeName} successfully executed.")]
    public static partial void LogJobSuccessfulExecuted(this ILogger logger, string jobTypeName);

    /// <summary>Logs that a background job threw an unhandled exception during execution.</summary>
    /// <param name="logger">The logger to emit through.</param>
    /// <param name="exception">The exception thrown by the job.</param>
    [LoggerMessage(
        EventId = LogEvents.BackgroundTasks.JobFailedExecuted,
        Level = LogLevel.Error,
        Message = "An error occurred while executing a background job.")]
    public static partial void LogJobFailedExecuted(this ILogger logger, Exception exception);
}
