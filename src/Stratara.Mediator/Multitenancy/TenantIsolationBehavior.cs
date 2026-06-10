using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Session;

namespace Stratara.Mediator.Multitenancy;

/// <summary>
/// Mediator pipeline behavior that enforces tenant isolation for requests of the
/// <see cref="IRequest{TResult}"/> shape that implement <see cref="ITenantScopedRequest"/>. Requests
/// that do not implement the marker pass through unchanged. Throws
/// <see cref="TenantAccessDeniedException"/> when the request's tenant does not match the session's
/// data-owner tenant, or (in strict mode) when an unauthorized cross-tenant operation is detected.
/// </summary>
/// <typeparam name="TRequest">The mediator request type passed through the pipeline.</typeparam>
/// <typeparam name="TResult">The result type returned by the inner handler.</typeparam>
[UsedImplicitly]
internal sealed class TenantIsolationBehavior<TRequest, TResult>(
    ISessionContextProvider sessionContextProvider,
    ICrossTenantAuthorizer crossTenantAuthorizer,
    TenantIsolationOptions options,
    ILogger<TenantIsolationBehavior<TRequest, TResult>> logger)
    : IPipelineBehavior<TRequest, TResult>
    where TRequest : IRequest<TResult>
{
    /// <summary>
    /// Enforces tenant isolation for <paramref name="request"/>, then delegates to <paramref name="next"/>.
    /// </summary>
    /// <param name="request">The incoming mediator request.</param>
    /// <param name="next">The continuation of the pipeline.</param>
    /// <param name="cancellationToken">Token to observe while authorizing and awaiting <paramref name="next"/>.</param>
    /// <returns>The result returned by the inner handler.</returns>
    /// <exception cref="TenantAccessDeniedException">The request failed the tenant-isolation check.</exception>
    public async Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken)
    {
        if (request is ITenantScopedRequest tenantScoped)
        {
            await TenantIsolationGuard.EnsureAuthorizedAsync(
                tenantScoped, sessionContextProvider, crossTenantAuthorizer, options, logger, cancellationToken);
        }

        return await next();
    }
}

/// <summary>
/// Mediator pipeline behavior that enforces tenant isolation for requests of the
/// <see cref="IRequest"/> shape (no result) that implement <see cref="ITenantScopedRequest"/>.
/// Requests that do not implement the marker pass through unchanged. Throws
/// <see cref="TenantAccessDeniedException"/> when the request's tenant does not match the session's
/// data-owner tenant, or (in strict mode) when an unauthorized cross-tenant operation is detected.
/// </summary>
/// <typeparam name="TRequest">The mediator request type passed through the pipeline.</typeparam>
[UsedImplicitly]
internal sealed class TenantIsolationBehavior<TRequest>(
    ISessionContextProvider sessionContextProvider,
    ICrossTenantAuthorizer crossTenantAuthorizer,
    TenantIsolationOptions options,
    ILogger<TenantIsolationBehavior<TRequest>> logger)
    : IPipelineBehavior<TRequest>
    where TRequest : IRequest
{
    /// <summary>
    /// Enforces tenant isolation for <paramref name="request"/>, then delegates to <paramref name="next"/>.
    /// </summary>
    /// <param name="request">The incoming mediator request.</param>
    /// <param name="next">The continuation of the pipeline.</param>
    /// <param name="cancellationToken">Token to observe while authorizing and awaiting <paramref name="next"/>.</param>
    /// <exception cref="TenantAccessDeniedException">The request failed the tenant-isolation check.</exception>
    public async Task HandleAsync(TRequest request, Func<Task> next, CancellationToken cancellationToken)
    {
        if (request is ITenantScopedRequest tenantScoped)
        {
            await TenantIsolationGuard.EnsureAuthorizedAsync(
                tenantScoped, sessionContextProvider, crossTenantAuthorizer, options, logger, cancellationToken);
        }

        await next();
    }
}
