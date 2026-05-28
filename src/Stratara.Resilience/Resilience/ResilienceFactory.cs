using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Stratara.Resilience;

internal static class ResilienceFactory
{
    private const int DefaultDispatcherRetryAttempts = 3;
    private static readonly TimeSpan DefaultDispatcherRetryDelay = TimeSpan.FromMilliseconds(200);

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

    private static void AddDispatcherRetry(ResiliencePipelineBuilder pipelineBuilder) =>
        pipelineBuilder.AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = DefaultDispatcherRetryAttempts,
            Delay = DefaultDispatcherRetryDelay,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true
        });
}
