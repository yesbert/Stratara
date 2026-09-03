namespace Stratara.Resilience;

/// <summary>
/// Stable names for Stratara's named Polly resilience pipelines. Resolve a pipeline via
/// <c>ResiliencePipelineProvider&lt;string&gt;.GetPipeline(...)</c> using one of these constants.
/// </summary>
public static class ResilienceNames
{
    /// <summary>
    /// Message-bus subscription / publish pipeline. Strategy: exponential retry up to
    /// <c>int.MaxValue</c>, 10s → 60s, jitter. Used by message-bus consumers and publishers.
    /// </summary>
    public const string MessageBus = "MessageBusPipeline";

    /// <summary>
    /// Command-dispatcher pipeline. Strategy: 3 retries, exponential, 200ms, jitter.
    /// Used when dispatching command envelopes through the outbox.
    /// </summary>
    public const string CommandDispatcher = "CommandDispatcherPipeline";

    /// <summary>
    /// Event-bundle-dispatcher pipeline. Strategy: 3 retries, exponential, 200ms, jitter.
    /// Used when dispatching event bundles through the outbox.
    /// </summary>
    public const string EventBundleDispatcher = "EventBundleDispatcherPipeline";

    /// <summary>
    /// Optimistic-concurrency-conflict pipeline. Strategy: retries <b>only</b> on
    /// <c>Stratara.Abstractions.Persistence.ConcurrencyConflictException</c> (5 attempts, short
    /// exponential backoff, jitter); any other exception propagates immediately. Intended for an
    /// in-process mediator request marked <c>IResilientRequest</c> whose handler re-reads and
    /// re-applies on a version clash.
    /// </summary>
    public const string ConcurrencyConflict = "ConcurrencyConflictPipeline";

    /// <summary>
    /// Pipeline that retries only <c>PrecedingFactMissingException</c> — a handler's report that the entity a
    /// fact refers to has not been applied yet — a bounded number of times with a short exponential backoff,
    /// about three seconds in total. Every other failure surfaces on the first occurrence. The projection and
    /// saga workers run every bundle under it.
    /// </summary>
    public const string PrecedingFact = "PrecedingFactPipeline";

    /// <summary>
    /// Pipeline the projection-replay worker runs each batch under: five attempts in all, exponential backoff
    /// from one second with jitter between them, retrying any exception except cancellation. A
    /// read-store timeout or a dropped connection mid-rebuild is retried; a failure that persists through
    /// every attempt ends the replay as an unretried one would.
    /// </summary>
    public const string ProjectionReplayBatch = "ProjectionReplayBatchPipeline";
}
