using System.Reflection;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Authorization;

namespace Stratara.Infrastructure.Authorization;

/// <summary>
/// Decorator over <see cref="ICommandOutboxDispatcher"/> that enforces <see cref="RequireRoleAttribute"/>
/// declarations on the command type before forwarding to the inner dispatcher.
/// </summary>
/// <remarks>
/// Roles are evaluated via the registered <see cref="IAuthorizationProvider"/>. The first missing
/// role short-circuits with an <see cref="AuthorizationException"/>; the inner dispatcher is never
/// invoked for unauthorized callers. Bulk enqueue (<see cref="EnqueueOutboxEntriesAsync"/>) is
/// pass-through because the caller is assumed to have already authorized the source command.
/// </remarks>
internal sealed class AuthorizingCommandOutboxDispatcher(
    ICommandOutboxDispatcher inner,
    IAuthorizationProvider authorizationProvider) : ICommandOutboxDispatcher
{
    /// <inheritdoc/>
    /// <exception cref="AuthorizationException">
    /// Thrown when the current principal is not in one of the roles required by
    /// <see cref="RequireRoleAttribute"/> declarations on <typeparamref name="T"/>.
    /// </exception>
    public async Task<Guid> EnqueueCommandAsync<T>(T command, CancellationToken cancellationToken = default)
        where T : ICommand
    {
        await AuthorizeAsync(typeof(T), cancellationToken);
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
    }
}
