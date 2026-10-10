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

    /// <summary>Logs that a bundle refers to an entity that has not been applied yet and will be retried.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The handler's report.</param>
    /// <param name="streamId">The stream the fact belongs to.</param>
    /// <param name="eventTypeName">The type name of the fact that could not be applied yet.</param>
    /// <param name="attempt">The attempt that failed, counting from one.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.PrecedingFactMissing,
        Level = LogLevel.Warning,
        Message = "Projection bundle refers to an entity not applied yet on stream {StreamId} ({EventTypeName}); attempt {Attempt}.")]
    public static partial void LogProjectionPrecedingFactMissing(this ILogger logger, Exception exception, Guid streamId, string eventTypeName, int attempt);

    /// <summary>
    /// Logs that a projection declaring <c>IForgetsDeletedTenants</c> reported a missing prerequisite for a
    /// fact of a tenant it has seen deleted, and that the fact was passed over.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="projection">The projection's name.</param>
    /// <param name="streamId">The stream the fact belongs to.</param>
    /// <param name="eventTypeName">The fact's type name.</param>
    /// <param name="tenantId">The deleted tenant that owns the fact.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ProjectionForgottenTenantFactPassedOver,
        Level = LogLevel.Information,
        Message = "Projection {Projection} passed over {EventTypeName} on stream {StreamId}: its tenant {TenantId} was deleted, and the projection forgot it.")]
    public static partial void LogProjectionForgottenTenantFactPassedOver(this ILogger logger, string projection, Guid streamId, string eventTypeName, Guid tenantId);

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

    /// <summary>Logs that a projection replay ended because its host stopped. It is not a failure.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="requestId">The identity of the request the replay ran.</param>
    /// <param name="replayedEvents">The number of events the replay had applied when it ended.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ProjectionReplayInterrupted,
        Level = LogLevel.Information,
        Message = "Projection replay {RequestId} interrupted by the host stopping after {ReplayedEvents} events. The read models are partly rebuilt — unless a host that keeps them restores them when it starts; otherwise request the replay again.")]
    public static partial void LogProjectionReplayInterrupted(this ILogger logger, Guid requestId, long replayedEvents);

    /// <summary>Logs, once per preparation, that a replay could not renew its marking; it tries again every second.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ProjectionReplayRenewalFailed,
        Level = LogLevel.Warning,
        Message = "Projection replay could not renew its marking while it prepares; it tries again every second. If the coordination store stays away longer than the lease, the marking lapses and publication resumes mid-rebuild.")]
    public static partial void LogProjectionReplayRenewalFailed(this ILogger logger, Exception exception);

    /// <summary>Logs that a replay request could not be claimed because the coordination state failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="requestId">The identity of the request.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ProjectionReplayRequestNotClaimed,
        Level = LogLevel.Error,
        Message = "Projection replay request {RequestId} could not be claimed; no replay started here.")]
    public static partial void LogProjectionReplayRequestNotClaimed(this ILogger logger, Exception exception, Guid requestId);

    /// <summary>Logs that the outcome of a replay could not be recorded in the coordination state.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="requestId">The identity of the request the replay ran.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ProjectionReplayOutcomeNotRecorded,
        Level = LogLevel.Error,
        Message = "The outcome of projection replay {RequestId} could not be recorded. Its marking lapses with its lease; the previous outcome stays readable.")]
    public static partial void LogProjectionReplayOutcomeNotRecorded(this ILogger logger, Exception exception, Guid requestId);

    /// <summary>Logs that the read models a replay is about to empty were preserved.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="requestId">The identity of the request the replay runs.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ReadModelsPreserved,
        Level = LogLevel.Information,
        Message = "Projection replay {RequestId}: the read models are preserved and are restored if the replay fails.")]
    public static partial void LogReadModelsPreserved(this ILogger logger, Guid requestId);

    /// <summary>Logs that a failed replay restored the read models to their state before it began.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="requestId">The identity of the request the replay ran.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ReadModelsRestored,
        Level = LogLevel.Warning,
        Message = "Projection replay {RequestId} failed; the read models were restored to their state before it began.")]
    public static partial void LogReadModelsRestored(this ILogger logger, Guid requestId);

    /// <summary>Logs that a state preserved by a replay whose host stopped was restored.</summary>
    /// <param name="logger">The logger.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.AbandonedReadModelsRestored,
        Level = LogLevel.Warning,
        Message = "A replay whose host stopped left preserved read models; they were restored to their state before that replay began.")]
    public static partial void LogAbandonedReadModelsRestored(this ILogger logger);

    /// <summary>Logs that the state preserved for a replay that succeeded could not be dropped.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="requestId">The identity of the request the replay ran.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.PreservedReadModelsNotDiscarded,
        Level = LogLevel.Warning,
        Message = "Projection replay {RequestId} succeeded, but the read models preserved before it could not be dropped. A host that starts later drops them where the coordination state still holds this outcome, and restores them otherwise — which undoes the replay.")]
    public static partial void LogPreservedReadModelsNotDiscarded(this ILogger logger, Exception exception, Guid requestId);

    /// <summary>Logs that preserved read models could not be restored; the preserved state is kept.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ReadModelRestoreFailed,
        Level = LogLevel.Error,
        Message = "The preserved read models could not be restored. The preserved state is kept; the read models hold what the replay rebuilt.")]
    public static partial void LogReadModelRestoreFailed(this ILogger logger, Exception exception);

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

    /// <summary>Logs that one projection-replay batch failed on an attempt. While the policy allows another attempt the batch is applied again from its start; after the last one the replay fails.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="afterSequence">The sequence number the batch starts after.</param>
    /// <param name="attempt">The attempt that failed, counted from one.</param>
    [LoggerMessage(
        EventId = LogEvents.Projection.ProjectionReplayBatchFailed,
        Level = LogLevel.Warning,
        Message = "Projection replay: batch after sequence {AfterSequence} failed on attempt {Attempt}. It is applied again from its start if the policy allows another attempt; otherwise the replay fails.")]
    public static partial void LogProjectionReplayBatchFailed(this ILogger logger, Exception exception, long afterSequence, int attempt);
}
