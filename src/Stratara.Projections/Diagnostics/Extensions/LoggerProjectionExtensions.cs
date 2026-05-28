using Microsoft.Extensions.Logging;
using Stratara.Diagnostics;

namespace Stratara.Shared.Diagnostics.Extensions;

/// <summary>Source-generated logger extensions for the projection worker, manager, and replay pipeline.</summary>
public static partial class LoggerProjectionExtensions
{
    /// <summary>Logs that the projection worker has started.</summary>
    /// <param name="logger">The logger.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ProjectionWorkerStarted,
        Level = LogLevel.Information,
        Message = "Starting Projection-Worker.")]
    public static partial void LogProjectionWorkerStarted(this ILogger logger);

    /// <summary>Logs that the projection worker is stopping.</summary>
    /// <param name="logger">The logger.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ProjectionWorkerStopped,
        Level = LogLevel.Information,
        Message = "Stopping Projection-Worker.")]
    public static partial void LogProjectionWorkerStopped(this ILogger logger);


    /// <summary>Logs that none of the supplied events were relevant for the given projection.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="eventCount">Number of events that were considered.</param>
    /// <param name="eventTypeNames">Deferred-formatting wrapper that renders distinct event type names from the considered batch.</param>
    /// <param name="projectionName">The projection name.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.EventsNotRelevantForProjection,
        Level = LogLevel.Debug,
        Message = "None of {EventCount} events ({EventTypeNames}) were relevant for {ProjectionName}.")]
    public static partial void LogEventsNotRelevantForProjection(this ILogger logger, int eventCount, DistinctEventTypeNames eventTypeNames, string projectionName);

    /// <summary>Logs that processing a projection threw an exception.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="projectionName">The projection that failed.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ProjectionFailed,
        Level = LogLevel.Error,
        Message = "Error while processing projection {ProjectionName}.")]
    public static partial void LogProjectionFailed(this ILogger logger, Exception exception, string projectionName);

    /// <summary>Logs that projection replay has started.</summary>
    /// <param name="logger">The logger.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ProjectionReplayStarted,
        Level = LogLevel.Information,
        Message = "Projection replay started.")]
    public static partial void LogProjectionReplayStarted(this ILogger logger);

    /// <summary>Logs that projection replay has completed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="totalEvents">The total number of events that were replayed.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ProjectionReplayCompleted,
        Level = LogLevel.Information,
        Message = "Projection replay completed: {TotalEvents} events replayed.")]
    public static partial void LogProjectionReplayCompleted(this ILogger logger, long totalEvents);

    /// <summary>Logs that all projection views have been truncated as part of a replay.</summary>
    /// <param name="logger">The logger.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ProjectionViewsTruncated,
        Level = LogLevel.Information,
        Message = "All projection views truncated.")]
    public static partial void LogProjectionViewsTruncated(this ILogger logger);

    /// <summary>Logs that a batch of events has been published during projection replay.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="count">The number of events in the batch.</param>
    /// <param name="lastSequence">The sequence number of the last event in the batch.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ProjectionReplayBatchPublished,
        Level = LogLevel.Information,
        Message = "Projection replay: processed batch of {Count} events, last sequence {LastSequence}.")]
    public static partial void LogProjectionReplayBatchPublished(this ILogger logger, int count, long lastSequence);

    /// <summary>Logs that projection replay failed with an exception.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ProjectionReplayFailed,
        Level = LogLevel.Error,
        Message = "Projection replay failed.")]
    public static partial void LogProjectionReplayFailed(this ILogger logger, Exception exception);
}
