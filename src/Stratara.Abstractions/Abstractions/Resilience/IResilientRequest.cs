namespace Stratara.Abstractions.Resilience;

/// <summary>
/// Opt-in marker for a mediator request (<see cref="Stratara.Abstractions.Mediator.ICommand"/>,
/// <see cref="Stratara.Abstractions.Mediator.ICommand{TResult}"/>, or
/// <see cref="Stratara.Abstractions.Mediator.IQuery{TResult}"/>) whose in-process dispatch should be
/// wrapped in a named Polly resilience pipeline — retry, timeout, circuit-breaker — applied by the
/// Stratara resilience pipeline behavior.
/// </summary>
/// <remarks>
/// <para>
/// The marker is inert until a consumer registers the resilience behavior
/// (<c>AddStrataraResilienceBehavior()</c>) and the named pipelines
/// (<c>AddResiliencePipelines()</c>, or its own <c>AddResiliencePipeline(name, ...)</c>
/// registrations). Requests that do not implement it pass through the behavior untouched.
/// </para>
/// <para>
/// <b>Side-effect caution:</b> a retrying pipeline re-invokes the handler, so only mark requests
/// whose handler is safe to re-run — naturally idempotent work, or operations guarded by
/// optimistic concurrency (point <see cref="ResiliencePipelineName"/> at a pipeline that retries
/// only on a concurrency conflict). Do not mark a request whose handler is not idempotent with a
/// blanket retry-on-any-exception pipeline.
/// </para>
/// </remarks>
public interface IResilientRequest
{
    /// <summary>
    /// The name of the registered Polly resilience pipeline to apply, resolved via
    /// <c>ResiliencePipelineProvider&lt;string&gt;.GetPipeline(name)</c>. Use one of the built-in
    /// <c>ResilienceNames</c> constants or a consumer-registered pipeline name.
    /// </summary>
    string ResiliencePipelineName { get; }
}
