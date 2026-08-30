using System.Reflection;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Session;

namespace Stratara.Infrastructure.Authorization;

/// <summary>
/// Decorator over <see cref="ICommandOutboxDispatcher"/> that enforces <see cref="RequireRoleAttribute"/>
/// and <see cref="RequirePermissionAttribute"/> declarations on the command type before forwarding to
/// the inner dispatcher.
/// </summary>
/// <remarks>
/// Roles are evaluated via the registered <see cref="IAuthorizationProvider"/>; permissions via the
/// registered <see cref="IPermissionResolver"/> against the ambient session's actor and data-owner
/// tenant. The first missing requirement short-circuits with an <see cref="AuthorizationException"/>
/// (permissions: <see cref="PermissionAuthorizationException"/>); the inner dispatcher is never
/// invoked for unauthorized callers. Bulk enqueue (<see cref="EnqueueOutboxEntriesAsync"/>) is
/// pass-through because the caller is assumed to have already authorized the source command.
/// </remarks>
internal sealed class AuthorizingCommandOutboxDispatcher(
    ICommandOutboxDispatcher inner,
    IAuthorizationProvider authorizationProvider,
    IPermissionResolver? permissionResolver = null,
    ISessionContextProvider? sessionContextProvider = null) : ICommandOutboxDispatcher
{
    /// <inheritdoc/>
    /// <exception cref="AuthorizationException">
    /// Thrown when the current principal is missing one of the roles or permissions required by
    /// <see cref="RequireRoleAttribute"/> / <see cref="RequirePermissionAttribute"/> declarations
    /// on <typeparamref name="T"/>.
    /// </exception>
    public async Task<Guid> EnqueueCommandAsync<T>(T command, CancellationToken cancellationToken = default)
        where T : ICommand
    {
        await AuthorizeAsync(command.GetType(), cancellationToken);
        return await inner.EnqueueCommandAsync(command, cancellationToken);
    }

    /// <inheritdoc/>
    public Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default)
    {
        return inner.EnqueueOutboxEntriesAsync(outboxEntries, cancellationToken);
    }

    private async Task AuthorizeAsync(Type commandType, CancellationToken cancellationToken)
    {
        var attributes = commandType.GetCustomAttributes<RequireRoleAttribute>();

        foreach (var role in attributes.Select(attribute => attribute.Role))
        {
            if (!await authorizationProvider.IsInRoleAsync(role, cancellationToken))
            {
                throw new AuthorizationException(role);
            }
        }

        await AuthorizePermissionsAsync(commandType, cancellationToken);
    }

    private async Task AuthorizePermissionsAsync(Type commandType, CancellationToken cancellationToken)
    {
        var required = commandType.GetCustomAttributes<RequirePermissionAttribute>()
            .Select(attribute => attribute.Permission)
            .ToList();

        if (required.Count == 0)
        {
            return;
        }

        if (permissionResolver is null)
        {
            throw new InvalidOperationException(
                $"Type '{commandType.FullName}' is decorated with [RequirePermission] but no IPermissionResolver " +
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
