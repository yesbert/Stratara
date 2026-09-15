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

        /// <summary>Instrument name <c>orleans.reader.applied</c>; see <see cref="OrleansReaderApplied"/>.</summary>
        public const string OrleansReaderAppliedName = "orleans.reader.applied";

        /// <summary>Instrument name <c>orleans.reader.stalled</c>; see <see cref="OrleansReaderStalled"/>.</summary>
        public const string OrleansReaderStalledName = "orleans.reader.stalled";

        /// <summary>
        /// Instrument name <c>orleans.reader.lag</c> — an observable gauge the Orleans execution model publishes
        /// on this meter: the seconds since the time recorded with the oldest entry a store reader has not
        /// applied, tagged with <see cref="MetricTags.Projection"/> and <see cref="MetricTags.Partition"/>.
        /// </summary>
        public const string OrleansReaderLagName = "orleans.reader.lag";

        /// <summary>Instrument name <c>orleans.intent.recorded</c>; see <see cref="OrleansIntentRecorded"/>.</summary>
        public const string OrleansIntentRecordedName = "orleans.intent.recorded";

        /// <summary>Instrument name <c>orleans.intent.resumed</c>; see <see cref="OrleansIntentResumed"/>.</summary>
        public const string OrleansIntentResumedName = "orleans.intent.resumed";

        /// <summary>Instrument name <c>orleans.intent.kept</c>; see <see cref="OrleansIntentKept"/>.</summary>
        public const string OrleansIntentKeptName = "orleans.intent.kept";

        /// <summary>Instrument name <c>orleans.completion.flushed</c>; see <see cref="OrleansCompletionFlushed"/>.</summary>
        public const string OrleansCompletionFlushedName = "orleans.completion.flushed";

        /// <summary>Instrument name <c>orleans.completion.failed</c>; see <see cref="OrleansCompletionFailed"/>.</summary>
        public const string OrleansCompletionFailedName = "orleans.completion.failed";

        /// <summary>Instrument name <c>orleans.heavy.permits_in_use</c>; see <see cref="OrleansHeavyPermitsInUse"/>.</summary>
        public const string OrleansHeavyPermitsInUseName = "orleans.heavy.permits_in_use";

        /// <summary>Instrument name <c>event_source.append.conflicts</c>; see <see cref="EventSourceAppendConflicts"/>.</summary>
        public const string EventSourceAppendConflictsName = "event_source.append.conflicts";

        /// <summary>Instrument name <c>event_source.events.appended</c>; see <see cref="EventsAppended"/>.</summary>
        public const string EventsAppendedName = "event_source.events.appended";

        /// <summary>Instrument name <c>outbox.published</c>; see <see cref="OutboxEntriesPublished"/>.</summary>
        public const string OutboxEntriesPublishedName = "outbox.published";

        /// <summary>Instrument name <c>messaging.dead_lettered</c>; see <see cref="MessagesDeadLettered"/>.</summary>
        public const string MessagesDeadLetteredName = "messaging.dead_lettered";

        /// <summary>Instrument name <c>command.duration</c>; see <see cref="CommandDuration"/>.</summary>
        public const string CommandDurationName = "command.duration";

        /// <summary>Instrument name <c>projection.events.processed</c>; see <see cref="ProjectionEventsProcessed"/>.</summary>
        public const string ProjectionEventsProcessedName = "projection.events.processed";

        /// <summary>Instrument name <c>projection.bundle.duration</c>; see <see cref="ProjectionBundleDuration"/>.</summary>
        public const string ProjectionBundleDurationName = "projection.bundle.duration";

        /// <summary>Instrument name <c>saga.events.processed</c>; see <see cref="SagaEventsProcessed"/>.</summary>
        public const string SagaEventsProcessedName = "saga.events.processed";

        /// <summary>Instrument name <c>saga.bundle.duration</c>; see <see cref="SagaBundleDuration"/>.</summary>
        public const string SagaBundleDurationName = "saga.bundle.duration";

        /// <summary>Instrument name <c>saga.inflight</c>; see <see cref="SagasInFlight"/>.</summary>
        public const string SagasInFlightName = "saga.inflight";

        /// <summary>The shared <see cref="Metrics.Meter"/> instance.</summary>
        public static readonly Meter Meter = new(MeterName, "1.0.0");

        /// <summary>
        /// Counter that tracks optimistic-concurrency conflicts encountered when appending
        /// events to a stream (write store).
        /// </summary>
        public static readonly Counter<long> EventSourceAppendConflicts = Meter.CreateCounter<long>(
            EventSourceAppendConflictsName,
            unit: "{conflict}",
            description: "Number of optimistic-concurrency conflicts detected when appending events to a stream.");

        /// <summary>
        /// Counter of domain events appended to an event stream. Tagged with
        /// <see cref="MetricTags.EventType"/> and <see cref="MetricTags.AggregateType"/> so operators can
        /// see per-event-type and per-aggregate write throughput.
        /// </summary>
        public static readonly Counter<long> EventsAppended = Meter.CreateCounter<long>(
            EventsAppendedName,
            unit: "{event}",
            description: "Number of domain events appended to an event stream, by event and aggregate type.");

        /// <summary>
        /// Counter of outbox entries successfully published by the outbox worker. Tagged with
        /// <see cref="MetricTags.OutboxKind"/> (<c>command</c> or <c>event</c>) so operators can
        /// distinguish command-dispatch from event-bundle throughput.
        /// </summary>
        public static readonly Counter<long> OutboxEntriesPublished = Meter.CreateCounter<long>(
            OutboxEntriesPublishedName,
            unit: "{entry}",
            description: "Number of outbox entries successfully published, by entry kind (command or event).");

        /// <summary>
        /// Counter of messages moved to a subscription's dead-letter destination after exhausting
        /// their redelivery bound. Tagged with <see cref="MetricTags.Topic"/>,
        /// <see cref="MetricTags.Subscription"/> and <see cref="MetricTags.Reason"/>
        /// (<c>conflict</c> or <c>failure</c>).
        /// </summary>
        public static readonly Counter<long> MessagesDeadLettered = Meter.CreateCounter<long>(
            MessagesDeadLetteredName,
            unit: "{message}",
            description: "Number of messages moved to a dead-letter destination, by topic, subscription and reason.");

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
            CommandDurationName,
            unit: "ms",
            description: "Latency in milliseconds of commands dispatched through the outbox worker, by request type and outcome.");

        /// <summary>
        /// Counter of events dispatched to projection handlers. Tagged with
        /// <see cref="MetricTags.EventType"/> and <see cref="MetricTags.Outcome"/> (<c>success</c> or
        /// <c>failure</c>) so operators can see per-event-type projection throughput and failure rates.
        /// </summary>
        public static readonly Counter<long> ProjectionEventsProcessed = Meter.CreateCounter<long>(
            ProjectionEventsProcessedName,
            unit: "{event}",
            description: "Number of events dispatched to projection handlers, by event type and outcome.");

        /// <summary>
        /// Histogram of projection event-bundle processing latency in milliseconds, measured around the
        /// projection worker's per-bundle dispatch. Tagged with <see cref="MetricTags.Outcome"/>
        /// (<c>success</c> or <c>failure</c>).
        /// </summary>
        public static readonly Histogram<double> ProjectionBundleDuration = Meter.CreateHistogram<double>(
            ProjectionBundleDurationName,
            unit: "ms",
            description: "Projection event-bundle processing latency in milliseconds, by outcome.");

        /// <summary>
        /// Counter of events dispatched to saga handlers. Tagged with
        /// <see cref="MetricTags.EventType"/> and <see cref="MetricTags.Outcome"/> (<c>success</c> or
        /// <c>failure</c>) so operators can see per-event-type saga throughput and failure rates.
        /// </summary>
        public static readonly Counter<long> SagaEventsProcessed = Meter.CreateCounter<long>(
            SagaEventsProcessedName,
            unit: "{event}",
            description: "Number of events dispatched to saga handlers, by event type and outcome.");

        /// <summary>
        /// Histogram of saga event-bundle processing latency in milliseconds, measured around the saga
        /// worker's per-bundle dispatch. Tagged with <see cref="MetricTags.Outcome"/> (<c>success</c> or
        /// <c>failure</c>).
        /// </summary>
        public static readonly Histogram<double> SagaBundleDuration = Meter.CreateHistogram<double>(
            SagaBundleDurationName,
            unit: "ms",
            description: "Saga event-bundle processing latency in milliseconds, by outcome.");

        /// <summary>
        /// Up/down counter tracking the number of saga event-bundles currently in flight (being
        /// processed across all saga-worker subscriptions). A persistently rising value indicates the
        /// saga lane cannot keep up with the inbound event rate.
        /// </summary>
        public static readonly UpDownCounter<long> SagasInFlight = Meter.CreateUpDownCounter<long>(
            SagasInFlightName,
            unit: "{bundle}",
            description: "Number of saga event-bundles currently being processed.");

        /// <summary>
        /// Counter of store entries a reader of the Orleans execution model applied. Tagged with
        /// <see cref="MetricTags.Projection"/> (the consumer) and <see cref="MetricTags.Partition"/>.
        /// </summary>
        public static readonly Counter<long> OrleansReaderApplied = Meter.CreateCounter<long>(
            OrleansReaderAppliedName,
            unit: "{entry}",
            description: "Number of store entries applied by the Orleans execution model's readers, by consumer and partition.");

        /// <summary>
        /// Up/down counter of partitions whose reader stopped at an entry it could not apply. Tagged with
        /// <see cref="MetricTags.Projection"/> and <see cref="MetricTags.Partition"/>; it rises when a
        /// partition stalls and falls when it advances again.
        /// </summary>
        public static readonly UpDownCounter<long> OrleansReaderStalled = Meter.CreateUpDownCounter<long>(
            OrleansReaderStalledName,
            unit: "{partition}",
            description: "Number of store-reader partitions stopped at an entry they could not apply, by consumer and partition.");

        /// <summary>Counter of commands recorded as durable intents before their dispatch returned.</summary>
        public static readonly Counter<long> OrleansIntentRecorded = Meter.CreateCounter<long>(
            OrleansIntentRecordedName,
            unit: "{command}",
            description: "Number of commands recorded as durable intents by the Orleans execution model.");

        /// <summary>Counter of recorded commands handed over again after their hand-over was lost.</summary>
        public static readonly Counter<long> OrleansIntentResumed = Meter.CreateCounter<long>(
            OrleansIntentResumedName,
            unit: "{command}",
            description: "Number of recorded commands resumed by the Orleans execution model's drain.");

        /// <summary>Counter of recorded commands kept for an operator after exhausting their resume bound.</summary>
        public static readonly Counter<long> OrleansIntentKept = Meter.CreateCounter<long>(
            OrleansIntentKeptName,
            unit: "{command}",
            description: "Number of recorded commands kept for an operator by the Orleans execution model.");

        /// <summary>Counter of completed intents removed from durable storage.</summary>
        public static readonly Counter<long> OrleansCompletionFlushed = Meter.CreateCounter<long>(
            OrleansCompletionFlushedName,
            unit: "{command}",
            description: "Number of completed intents removed by the Orleans execution model.");

        /// <summary>Counter of removals of completed intents that failed and were left to the drain.</summary>
        public static readonly Counter<long> OrleansCompletionFailed = Meter.CreateCounter<long>(
            OrleansCompletionFailedName,
            unit: "{flush}",
            description: "Number of failed removals of completed intents in the Orleans execution model.");

        /// <summary>Up/down counter of heavy-work permits currently held across the cluster.</summary>
        public static readonly UpDownCounter<long> OrleansHeavyPermitsInUse = Meter.CreateUpDownCounter<long>(
            OrleansHeavyPermitsInUseName,
            unit: "{permit}",
            description: "Number of heavy-work permits held in the Orleans execution model.");
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

        /// <summary>Tag name <c>messaging.topic</c> — the topic a message was published to.</summary>
        public const string Topic = "messaging.topic";

        /// <summary>Tag name <c>messaging.subscription</c> — the subscription a message was consumed under.</summary>
        public const string Subscription = "messaging.subscription";

        /// <summary>Tag name <c>reason</c> — why a message was dead-lettered: <c>conflict</c> or <c>failure</c>.</summary>
        public const string Reason = "reason";

        /// <summary>Tag name <c>projection</c> — the consumer a store reader applies for: a projection's name, or <c>sagas</c>.</summary>
        public const string Projection = "projection";

        /// <summary>Tag name <c>partition</c> — the store partition a reader reads.</summary>
        public const string Partition = "partition";

        /// <summary>
        /// The value an <see cref="EventType"/> or <see cref="AggregateType"/> tag carries for a type name: the
        /// type's simple name, as <c>Type.Name</c> returns it, whether <paramref name="typeName"/> is
        /// simple, namespace-qualified or assembly-qualified. Every instrument tags a type with this value, so
        /// series from the write side and the read side join on it.
        /// </summary>
        /// <param name="typeName">A type name in any of those forms.</param>
        /// <returns>The simple name — the part after the last namespace or nesting separator, without the assembly or generic arguments.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="typeName"/> is <see langword="null"/>.</exception>
        public static string TypeNameValue(string typeName)
        {
            ArgumentNullException.ThrowIfNull(typeName);
            var end = typeName.IndexOfAny([',', '[']);
            var fullName = end < 0 ? typeName : typeName[..end];
            var start = fullName.LastIndexOfAny(['.', '+']) + 1;
            return fullName[start..].Trim();
        }
    }
}
