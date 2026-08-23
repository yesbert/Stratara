using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Stratara.Diagnostics;

/// <summary>
/// Shared observability primitives — <see cref="ActivitySource"/> + <see cref="Meter"/> +
/// stable tag-name + metric-name constants used across every Stratara package.
/// </summary>
/// <remarks>
/// The source name <c>"Stratara.Application"</c> and meter name <c>"Stratara.Service"</c> are
/// part of the public observability contract — renaming them breaks downstream
/// OTel-collector / Grafana / Tempo queries. Treat these constants as a stable surface.
/// </remarks>
public static class ApplicationDiagnostics
{
    /// <summary>Tag name <c>correlation.id</c> for cross-service request correlation.</summary>
    public const string CorrelationIdTagName = "correlation.id";

    /// <summary>Tag name <c>causation.id</c> for the immediate-cause request id.</summary>
    public const string CausationIdTagName = "causation.id";

    /// <summary>Tag name <c>tenant.id</c> — the data owner (Subject) tenant id.</summary>
    public const string TenantIdTagName = "tenant.id";

    /// <summary>Tag name <c>user.id</c> — the Actor user id (who triggered the operation).</summary>
    public const string UserIdTagName = "user.id";

    /// <summary>Stratara's shared <see cref="ActivitySource"/>.</summary>
    public static class Activity
    {
        /// <summary>The activity source name — <c>"Stratara.Application"</c>.</summary>
        public const string SourceName = "Stratara.Application";

        /// <summary>The shared <see cref="ActivitySource"/> instance.</summary>
        public static readonly ActivitySource Source = new(SourceName);
    }

    /// <summary>Stratara's shared <see cref="Metrics.Meter"/> and instruments.</summary>
    public static class Metrics
    {
        /// <summary>The meter name — <c>"Stratara.Service"</c>.</summary>
        public const string MeterName = "Stratara.Service";

        /// <summary>The shared <see cref="Metrics.Meter"/> instance.</summary>
        public static readonly Meter Meter = new(MeterName, "1.0.0");

        /// <summary>
        /// Counter that tracks optimistic-concurrency conflicts encountered when appending
        /// events to a stream (write store).
        /// </summary>
        public static readonly Counter<long> EventSourceAppendConflicts = Meter.CreateCounter<long>(
            "event_source.append.conflicts",
            unit: "{conflict}",
            description: "Number of optimistic-concurrency conflicts detected when appending events to a stream.");

        /// <summary>
        /// Counter of domain events appended to an event stream. Tagged with
        /// <see cref="MetricTags.EventType"/> and <see cref="MetricTags.AggregateType"/> so operators can
        /// see per-event-type and per-aggregate write throughput.
        /// </summary>
        public static readonly Counter<long> EventsAppended = Meter.CreateCounter<long>(
            "event_source.events.appended",
            unit: "{event}",
            description: "Number of domain events appended to an event stream, by event and aggregate type.");

        /// <summary>
        /// Counter of outbox entries successfully published by the outbox worker. Tagged with
        /// <see cref="MetricTags.OutboxKind"/> (<c>command</c> or <c>event</c>) so operators can
        /// distinguish command-dispatch from event-bundle throughput.
        /// </summary>
        public static readonly Counter<long> OutboxEntriesPublished = Meter.CreateCounter<long>(
            "outbox.published",
            unit: "{entry}",
            description: "Number of outbox entries successfully published, by entry kind (command or event).");

        /// <summary>
        /// Histogram of command-handling latency in milliseconds for commands dispatched **through the
        /// outbox worker**. Tagged with <see cref="MetricTags.RequestType"/> and
        /// <see cref="MetricTags.Outcome"/> (<c>success</c> or <c>failure</c>).
        /// </summary>
        /// <remarks>
        /// It is not end-to-end command latency, despite the name. The in-process mediator dispatch
        /// path records nothing here, so a host that dispatches commands directly sees an empty
        /// histogram and a host that does both sees only half its traffic. Read it as "outbox command
        /// latency". Covering the in-process path would change what the instrument means and is a
        /// separate decision.
        /// </remarks>
        public static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>(
            "command.duration",
            unit: "ms",
            description: "Latency in milliseconds of commands dispatched through the outbox worker, by request type and outcome.");

        /// <summary>
        /// Counter of events dispatched to projection handlers. Tagged with
        /// <see cref="MetricTags.EventType"/> and <see cref="MetricTags.Outcome"/> (<c>success</c> or
        /// <c>failure</c>) so operators can see per-event-type projection throughput and failure rates.
        /// </summary>
        public static readonly Counter<long> ProjectionEventsProcessed = Meter.CreateCounter<long>(
            "projection.events.processed",
            unit: "{event}",
            description: "Number of events dispatched to projection handlers, by event type and outcome.");

        /// <summary>
        /// Histogram of projection event-bundle processing latency in milliseconds, measured around the
        /// projection worker's per-bundle dispatch. Tagged with <see cref="MetricTags.Outcome"/>
        /// (<c>success</c> or <c>failure</c>).
        /// </summary>
        public static readonly Histogram<double> ProjectionBundleDuration = Meter.CreateHistogram<double>(
            "projection.bundle.duration",
            unit: "ms",
            description: "Projection event-bundle processing latency in milliseconds, by outcome.");

        /// <summary>
        /// Counter of events dispatched to saga handlers. Tagged with
        /// <see cref="MetricTags.EventType"/> and <see cref="MetricTags.Outcome"/> (<c>success</c> or
        /// <c>failure</c>) so operators can see per-event-type saga throughput and failure rates.
        /// </summary>
        public static readonly Counter<long> SagaEventsProcessed = Meter.CreateCounter<long>(
            "saga.events.processed",
            unit: "{event}",
            description: "Number of events dispatched to saga handlers, by event type and outcome.");

        /// <summary>
        /// Histogram of saga event-bundle processing latency in milliseconds, measured around the saga
        /// worker's per-bundle dispatch. Tagged with <see cref="MetricTags.Outcome"/> (<c>success</c> or
        /// <c>failure</c>).
        /// </summary>
        public static readonly Histogram<double> SagaBundleDuration = Meter.CreateHistogram<double>(
            "saga.bundle.duration",
            unit: "ms",
            description: "Saga event-bundle processing latency in milliseconds, by outcome.");

        /// <summary>
        /// Up/down counter tracking the number of saga event-bundles currently in flight (being
        /// processed across all saga-worker subscriptions). A persistently rising value indicates the
        /// saga lane cannot keep up with the inbound event rate.
        /// </summary>
        public static readonly UpDownCounter<long> SagasInFlight = Meter.CreateUpDownCounter<long>(
            "saga.inflight",
            unit: "{bundle}",
            description: "Number of saga event-bundles currently being processed.");
    }

    /// <summary>
    /// Stable string values for the <see cref="MetricTags.Outcome"/> tag, so instrument
    /// call-sites never hard-code the literal.
    /// </summary>
    public static class Outcomes
    {
        /// <summary>Outcome tag value for a successful operation — <c>success</c>.</summary>
        public const string Success = "success";

        /// <summary>Outcome tag value for a failed operation — <c>failure</c>.</summary>
        public const string Failure = "failure";
    }

    /// <summary>
    /// Stable string values for the <see cref="MetricTags.OutboxKind"/> tag, so instrument
    /// call-sites never hard-code the literal.
    /// </summary>
    public static class OutboxKinds
    {
        /// <summary>Outbox-kind tag value for a dispatched command — <c>command</c>.</summary>
        public const string Command = "command";

        /// <summary>Outbox-kind tag value for a published event bundle — <c>event</c>.</summary>
        public const string Event = "event";
    }

    /// <summary>Tag name constants used on metric instruments.</summary>
    public static class MetricTags
    {
        /// <summary>Tag name <c>aggregate.type</c>.</summary>
        public const string AggregateType = "aggregate.type";

        /// <summary>Tag name <c>bucket.id</c> — bucket-lock bucket index.</summary>
        public const string BucketId = "bucket.id";

        /// <summary>Tag name <c>event.type</c> — the simple name of a domain-event type.</summary>
        public const string EventType = "event.type";

        /// <summary>Tag name <c>request.type</c> — the simple name of a command/query request type.</summary>
        public const string RequestType = "request.type";

        /// <summary>
        /// Tag name <c>outcome</c> — the result of an operation; see <see cref="Outcomes"/> for values.
        /// </summary>
        public const string Outcome = "outcome";

        /// <summary>Tag name <c>outbox.kind</c> — <c>command</c> or <c>event</c>.</summary>
        public const string OutboxKind = "outbox.kind";
    }
}
