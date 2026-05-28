using Microsoft.Extensions.Logging;
using Stratara.Diagnostics;

namespace Stratara.Shared.Diagnostics.Extensions;

/// <summary>
/// Source-generated structured-logging helpers for event-bundle integrity verification. Used by
/// every worker that consumes <c>EventBundle</c>s off the message bus (projection, saga).
/// Backed by <see cref="LoggerMessageAttribute"/> with stable event ids from
/// <see cref="LogEvents.EventBundleIntegrity"/>.
/// </summary>
/// <remarks>
/// <c>EventBundle</c> carries no top-level <c>Id</c> property — bundles are identified by
/// the id of their first event plus the bundle event count. That pair is unique enough for
/// forensic correlation against the event-stream table without forcing a new wire-format field.
/// </remarks>
public static partial class LoggerEventBundleExtensions
{
    /// <summary>Logs that an event bundle failed integrity verification under Permissive mode and is still being dispatched.</summary>
    /// <param name="logger">The logger to emit through.</param>
    /// <param name="firstEventId">Id of the first event in the bundle.</param>
    /// <param name="eventCount">Number of events in the bundle.</param>
    [LoggerMessage(
        EventId = LogEvents.EventBundleIntegrity.IntegrityWarning,
        Level = LogLevel.Warning,
        Message = "Event bundle (first event {FirstEventId}, {EventCount} events) integrity verification failed (Permissive mode) — dispatching but signature mismatch logged.")]
    public static partial void LogEventBundleIntegrityWarning(this ILogger logger, Guid firstEventId, int eventCount);

    /// <summary>Logs that an event bundle was rejected because integrity verification failed under Strict mode.</summary>
    /// <param name="logger">The logger to emit through.</param>
    /// <param name="firstEventId">Id of the first event in the rejected bundle.</param>
    /// <param name="eventCount">Number of events in the rejected bundle.</param>
    [LoggerMessage(
        EventId = LogEvents.EventBundleIntegrity.IntegrityRejected,
        Level = LogLevel.Error,
        Message = "Event bundle (first event {FirstEventId}, {EventCount} events) integrity verification failed (Strict mode) — rejecting bundle.")]
    public static partial void LogEventBundleIntegrityRejected(this ILogger logger, Guid firstEventId, int eventCount);
}
