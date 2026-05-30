using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Validation;

namespace Stratara.Validation;

/// <summary>
/// Mediator pipeline behavior that validates a request of the <see cref="IRequest{TResult}"/>
/// shape before the handler runs. Throws <see cref="StrataraValidationException"/> when any
/// <see cref="ValidationSeverity.Error"/> failure is detected; otherwise delegates to the inner
/// pipeline unchanged.
/// </summary>
/// <typeparam name="TRequest">The mediator request type passed through the pipeline.</typeparam>
/// <typeparam name="TResult">The result type returned by the inner handler.</typeparam>
[UsedImplicitly]
internal sealed class ValidationPipelineBehavior<TRequest, TResult>(
    IEnumerable<IValidator<TRequest>> validators,
    ILogger<ValidationPipelineBehavior<TRequest, TResult>> logger)
    : IPipelineBehavior<TRequest, TResult>
    where TRequest : IRequest<TResult>
{
    /// <summary>
    /// Validates <paramref name="request"/>, then delegates to <paramref name="next"/> and returns its result.
    /// </summary>
    /// <param name="request">The incoming mediator request.</param>
    /// <param name="next">The continuation of the pipeline.</param>
    /// <param name="cancellationToken">Token to observe while validating and awaiting <paramref name="next"/>.</param>
    /// <returns>The result returned by the inner handler.</returns>
    /// <exception cref="StrataraValidationException">A validator produced one or more <see cref="ValidationSeverity.Error"/> failures.</exception>
    public async Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken)
    {
        await ValidationRunner.EnsureValidAsync(validators, request, logger, cancellationToken);
        return await next();
    }
}

/// <summary>
/// Mediator pipeline behavior that validates a request of the <see cref="IRequest"/> shape
/// (no result) before the handler runs. Throws <see cref="StrataraValidationException"/> when any
/// <see cref="ValidationSeverity.Error"/> failure is detected; otherwise delegates to the inner
/// pipeline unchanged.
/// </summary>
/// <typeparam name="TRequest">The mediator request type passed through the pipeline.</typeparam>
[UsedImplicitly]
internal sealed class ValidationPipelineBehavior<TRequest>(
    IEnumerable<IValidator<TRequest>> validators,
    ILogger<ValidationPipelineBehavior<TRequest>> logger)
    : IPipelineBehavior<TRequest>
    where TRequest : IRequest
{
    /// <summary>
    /// Validates <paramref name="request"/>, then delegates to <paramref name="next"/>.
    /// </summary>
    /// <param name="request">The incoming mediator request.</param>
    /// <param name="next">The continuation of the pipeline.</param>
    /// <param name="cancellationToken">Token to observe while validating and awaiting <paramref name="next"/>.</param>
    /// <exception cref="StrataraValidationException">A validator produced one or more <see cref="ValidationSeverity.Error"/> failures.</exception>
    public async Task HandleAsync(TRequest request, Func<Task> next, CancellationToken cancellationToken)
    {
        await ValidationRunner.EnsureValidAsync(validators, request, logger, cancellationToken);
        await next();
    }
}
