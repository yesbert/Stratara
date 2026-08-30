using System.Reflection;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Session;

namespace Stratara.Mediator.Authorization;

internal sealed class AuthorizingMediator(
    IMediator inner,
    IAuthorizationProvider authorizationProvider,
    IPermissionResolver? permissionResolver = null,
    ISessionContextProvider? sessionContextProvider = null) : IAuthorizingMediator
{
    public async Task<TResult> HandleAsync<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(request.GetType(), cancellationToken);
        return await inner.HandleAsync(request, cancellationToken);
    }

    public async Task HandleAsync<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : class, IRequest
    {
        await AuthorizeAsync(request.GetType(), cancellationToken);
        await inner.HandleAsync(request, cancellationToken);
    }

    private async Task AuthorizeAsync(Type requestType, CancellationToken cancellationToken)
    {
        await AuthorizeRolesAsync(requestType, cancellationToken);
        await AuthorizePermissionsAsync(requestType, cancellationToken);
    }

    private async Task AuthorizeRolesAsync(Type requestType, CancellationToken cancellationToken)
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

    private async Task AuthorizePermissionsAsync(Type requestType, CancellationToken cancellationToken)
    {
        var required = requestType.GetCustomAttributes<RequirePermissionAttribute>()
            .Select(attribute => attribute.Permission)
            .ToList();

        if (required.Count == 0)
        {
            return;
        }

        if (permissionResolver is null)
        {
            throw new InvalidOperationException(
                $"Type '{requestType.FullName}' is decorated with [RequirePermission] but no IPermissionResolver " +
                "is registered. Register one (e.g. AddPermissionCatalog(...) + AddCatalogPermissionResolver<TUser>()) " +
                "so permission attributes can be enforced.");
        }

        var session = sessionContextProvider?.Current
                      ?? throw new PermissionAuthorizationException(required[0]);

        var held = await permissionResolver.ResolvePermissionsAsync(
            session.ActorUserId, session.TenantId, cancellationToken);

        foreach (var permission in required.Where(permission => !held.Contains(permission)))
        {
            throw new PermissionAuthorizationException(permission);
        }
    }
}
