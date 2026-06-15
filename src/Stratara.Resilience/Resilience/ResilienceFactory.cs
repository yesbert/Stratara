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

    /// <remarks>
    /// The message-bus pipeline retries indefinitely on purpose: a transient bus outage must not drop
    /// messages, and the outbox pattern in CommandOutboxDispatcher / EventBundleOutboxDispatcher persists
    /// before publish so at-least-once is preserved. To bound the duty cycle during a pathological loop
    /// (e.g. permanently misconfigured broker URL) we wrap the retry in a circuit breaker that opens
    /// after 10 consecutive failures within 60 s and stays open for 60 s before half-opening — so a
    /// permanent failure surfaces in metrics + logs at roughly one breaker-cycle per minute instead of
    /// the unbounded retry storm the audit (F-005) flagged.
    /// </remarks>
    public static void CreateMessageBusPipeline(ResiliencePipelineBuilder pipelineBuilder)
    {
        pipelineBuilder
            .AddRetry(new RetryStrategyOptions
            {
                BackoffType = DelayBackoffType.Exponential,
                MaxRetryAttempts = int.MaxValue,
                Delay = TimeSpan.FromSeconds(10),
                MaxDelay = TimeSpan.FromSeconds(60),
                UseJitter = true
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 1.0,
                MinimumThroughput = 10,
                SamplingDuration = TimeSpan.FromSeconds(60),
                BreakDuration = TimeSpan.FromSeconds(60),
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
