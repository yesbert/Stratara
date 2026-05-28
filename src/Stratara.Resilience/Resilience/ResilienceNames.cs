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
}
