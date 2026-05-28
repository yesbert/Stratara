using System.Reflection;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Authorization;

namespace Stratara.Mediator.Authorization;

internal sealed class AuthorizingMediator(IMediator inner, IAuthorizationProvider authorizationProvider) : IAuthorizingMediator
{
    public async Task<TResult> HandleAsync<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(request.GetType(), cancellationToken);
        return await inner.HandleAsync(request, cancellationToken);
    }

    public async Task HandleAsync<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : class, IRequest
    {
        await AuthorizeAsync(typeof(TRequest), cancellationToken);
        await inner.HandleAsync(request, cancellationToken);
    }

    private async Task AuthorizeAsync(Type requestType, CancellationToken cancellationToken)
    {
        var attributes = requestType.GetCustomAttributes<RequireRoleAttribute>();

        foreach (var role in attributes.Select(attribute => attribute.Role))
        {
            if (!await authorizationProvider.IsInRoleAsync(role, cancellationToken))
            {
                throw new AuthorizationException(role);
            }
        }
    }
}
