using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Stratara.Abstractions.Persistence;

namespace Stratara.Resilience;

internal static class ResilienceFactory
{
    private const int DefaultDispatcherRetryAttempts = 3;
    private static readonly TimeSpan DefaultDispatcherRetryDelay = TimeSpan.FromMilliseconds(200);

    private const int ConcurrencyConflictRetryAttempts = 5;
    private static readonly TimeSpan ConcurrencyConflictRetryDelay = TimeSpan.FromMilliseconds(50);

    internal static readonly TimeSpan MessageBusBaseDelay = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MessageBusMaxDelay = TimeSpan.FromSeconds(60);
    internal const int MessageBusMinimumThroughput = 5;

    // Derived, not chosen: at steady state the retry produces one action per MessageBusMaxDelay, so a
    // window narrower than MaxDelay × MinimumThroughput can never see the throughput the breaker
    // demands. Doubling leaves headroom for jitter. With the values above: 60s × 5 × 2 = 10 minutes.
    internal static readonly TimeSpan MessageBusSamplingDuration =
        MessageBusMaxDelay * MessageBusMinimumThroughput * 2;
    internal static readonly TimeSpan MessageBusBreakDuration = TimeSpan.FromSeconds(60);

    /// <remarks>
    /// The message-bus pipeline retries indefinitely on purpose: a transient bus outage must not drop
    /// messages, and the outbox pattern in CommandOutboxDispatcher / EventBundleOutboxDispatcher persists
    /// before publish so at-least-once is preserved. The duty cycle during a pathological loop (e.g. a
    /// permanently misconfigured broker URL) is bounded by the 60 s delay cap, not by the breaker.
    ///
    /// The breaker's job is to make a sustained outage <i>visible</i>: five consecutive failures inside
    /// a ten-minute window open the circuit, which surfaces in metrics and logs as a state an operator
    /// can alert on, rather than only as a slow retry loop. It stays open for 60 s before half-opening;
    /// a longer break would only delay recovery, since half-open probes are what notice the broker
    /// returning. The retry sits in front of the breaker and retries BrokenCircuitException too, so an
    /// open circuit never turns into a dropped message.
    ///
    /// The sampling window is derived from the retry's own delay cap rather than chosen independently —
    /// see MessageBusSamplingDuration. Three unrelated constants are what made this breaker unable to
    /// open at all until 3.4.0, despite this remark claiming otherwise.
    /// </remarks>
    public static void CreateMessageBusPipeline(ResiliencePipelineBuilder pipelineBuilder)
    {
        pipelineBuilder
            .AddRetry(new RetryStrategyOptions
            {
                BackoffType = DelayBackoffType.Exponential,
                MaxRetryAttempts = int.MaxValue,
                Delay = MessageBusBaseDelay,
                MaxDelay = MessageBusMaxDelay,
                UseJitter = true
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 1.0,
                MinimumThroughput = MessageBusMinimumThroughput,
                SamplingDuration = MessageBusSamplingDuration,
                BreakDuration = MessageBusBreakDuration,
            });
    }

    public static void CreateCommandDispatcherPipeline(ResiliencePipelineBuilder pipelineBuilder) =>
        AddDispatcherRetry(pipelineBuilder);

    public static void CreateEventBundleDispatcherPipeline(ResiliencePipelineBuilder pipelineBuilder) =>
        AddDispatcherRetry(pipelineBuilder);

    /// <remarks>
    /// Retries only on <see cref="ConcurrencyConflictException"/> — the provider-agnostic
    /// optimistic-concurrency signal — so a handler that re-reads and re-applies on a version clash
    /// succeeds after a transient conflict. Any other exception is left to propagate. Short backoff
    /// (50ms exponential + jitter) because a concurrency conflict resolves as soon as the competing
    /// writer commits; five attempts bound the in-process retry budget.
    /// </remarks>
    public static void CreateConcurrencyConflictPipeline(ResiliencePipelineBuilder pipelineBuilder) =>
        pipelineBuilder.AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<ConcurrencyConflictException>(),
            MaxRetryAttempts = ConcurrencyConflictRetryAttempts,
            Delay = ConcurrencyConflictRetryDelay,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true
        });

    private static void AddDispatcherRetry(ResiliencePipelineBuilder pipelineBuilder) =>
        pipelineBuilder.AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = DefaultDispatcherRetryAttempts,
            Delay = DefaultDispatcherRetryDelay,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true
        });
}
