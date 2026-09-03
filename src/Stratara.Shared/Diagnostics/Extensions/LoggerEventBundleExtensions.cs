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
    /// <summary>Logs that an event bundle carried a signature that did not verify, and is still being dispatched under Permissive mode.</summary>
    /// <param name="logger">The logger to emit through.</param>
    /// <param name="firstEventId">Id of the first event in the bundle.</param>
    /// <param name="eventCount">Number of events in the bundle.</param>
    [LoggerMessage(
        EventId = LogEvents.EventBundleIntegrity.IntegrityWarning,
        Level = LogLevel.Warning,
        Message = "Event bundle (first event {FirstEventId}, {EventCount} events) carries a signature that does not verify (Permissive mode) — dispatching anyway. The publisher holds a different key, or the bundle was altered in transit.")]
    public static partial void LogEventBundleIntegrityWarning(this ILogger logger, Guid firstEventId, int eventCount);

    /// <summary>Logs that an event bundle carried no signature, and is still being dispatched under Permissive mode.</summary>
    /// <param name="logger">The logger to emit through.</param>
    /// <param name="firstEventId">Id of the first event in the bundle.</param>
    /// <param name="eventCount">Number of events in the bundle.</param>
    [LoggerMessage(
        EventId = LogEvents.EventBundleIntegrity.UnsignedWarning,
        Level = LogLevel.Warning,
        Message = "Event bundle (first event {FirstEventId}, {EventCount} events) carries no signature (Permissive mode) — dispatching anyway. Expected while publishers are still being rolled over to signing; afterwards it means a publisher was missed.")]
    public static partial void LogEventBundleUnsignedWarning(this ILogger logger, Guid firstEventId, int eventCount);

    /// <summary>Logs that an event bundle was rejected under Strict mode because its signature did not verify.</summary>
    /// <param name="logger">The logger to emit through.</param>
    /// <param name="firstEventId">Id of the first event in the rejected bundle.</param>
    /// <param name="eventCount">Number of events in the rejected bundle.</param>
    [LoggerMessage(
        EventId = LogEvents.EventBundleIntegrity.IntegrityRejected,
        Level = LogLevel.Error,
        Message = "Event bundle (first event {FirstEventId}, {EventCount} events) carries a signature that does not verify (Strict mode) — rejecting bundle.")]
    public static partial void LogEventBundleIntegrityRejected(this ILogger logger, Guid firstEventId, int eventCount);

    /// <summary>Logs that an event bundle was rejected under Strict mode because it carried no signature.</summary>
    /// <param name="logger">The logger to emit through.</param>
    /// <param name="firstEventId">Id of the first event in the rejected bundle.</param>
    /// <param name="eventCount">Number of events in the rejected bundle.</param>
    [LoggerMessage(
        EventId = LogEvents.EventBundleIntegrity.UnsignedRejected,
        Level = LogLevel.Error,
        Message = "Event bundle (first event {FirstEventId}, {EventCount} events) carries no signature (Strict mode) — rejecting bundle. A publisher is not signing.")]
    public static partial void LogEventBundleUnsignedRejected(this ILogger logger, Guid firstEventId, int eventCount);
}
