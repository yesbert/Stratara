using Polly.Registry;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Resilience;

namespace Stratara.Resilience.Mediator;

/// <summary>
/// Mediator pipeline behavior that wraps the dispatch of an <see cref="IResilientRequest"/>-marked
/// command/query (with result) in the named Polly resilience pipeline the request selects. Requests
/// that do not implement <see cref="IResilientRequest"/> pass straight through.
/// </summary>
/// <remarks>
/// Resolve the pipeline lazily per request via <see cref="ResiliencePipelineProvider{TKey}"/> so the
/// behavior can be a single open generic registration. Register with
/// <c>AddStrataraResilienceBehavior()</c> after the validation / tenant-isolation behaviors so the
/// retry wraps the handler rather than re-running guard behaviors.
/// </remarks>
/// <typeparam name="TRequest">The dispatched request type.</typeparam>
/// <typeparam name="TResult">The result type produced by the handler.</typeparam>
/// <param name="pipelineProvider">Polly registry used to resolve the named resilience pipeline.</param>
internal sealed class ResiliencePipelineBehavior<TRequest, TResult>(
    ResiliencePipelineProvider<string> pipelineProvider)
    : IPipelineBehavior<TRequest, TResult>
    where TRequest : IRequest<TResult>
{
    /// <inheritdoc/>
    public async Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken)
    {
        if (request is not IResilientRequest resilient)
        {
            return await next();
        }

        var pipeline = pipelineProvider.GetPipeline(resilient.ResiliencePipelineName);
        return await pipeline.ExecuteAsync(async _ => await next(), cancellationToken);
    }
}

/// <summary>
/// Mediator pipeline behavior that wraps the dispatch of an <see cref="IResilientRequest"/>-marked
/// command (without result) in the named Polly resilience pipeline the request selects. Requests
/// that do not implement <see cref="IResilientRequest"/> pass straight through.
/// </summary>
/// <typeparam name="TRequest">The dispatched request type.</typeparam>
/// <param name="pipelineProvider">Polly registry used to resolve the named resilience pipeline.</param>
internal sealed class ResiliencePipelineBehavior<TRequest>(
    ResiliencePipelineProvider<string> pipelineProvider)
    : IPipelineBehavior<TRequest>
    where TRequest : IRequest
{
    /// <inheritdoc/>
    public async Task HandleAsync(TRequest request, Func<Task> next, CancellationToken cancellationToken)
    {
        if (request is not IResilientRequest resilient)
        {
            await next();
            return;
        }

        var pipeline = pipelineProvider.GetPipeline(resilient.ResiliencePipelineName);
        await pipeline.ExecuteAsync(async _ => await next(), cancellationToken);
    }
}
