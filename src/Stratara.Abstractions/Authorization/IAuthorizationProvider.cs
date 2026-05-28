namespace Stratara.Abstractions.Authorization;

/// <summary>
/// Provides role-membership checks for the authorizing mediator + command-outbox-dispatcher
/// decorators. Implementations typically read from <c>HttpContext.User</c>, a JWT claim set,
/// or a tenant-scoped identity service.
/// </summary>
/// <example>
/// Minimal ASP.NET Core implementation that reads from the current
/// <c>HttpContext.User</c>:
/// <code>
/// public sealed class HttpContextAuthorizationProvider(IHttpContextAccessor accessor) : IAuthorizationProvider
/// {
///     public Task&lt;bool&gt; IsInRoleAsync(string role, CancellationToken cancellationToken = default) =&gt;
///         Task.FromResult(accessor.HttpContext?.User?.IsInRole(role) ?? false);
/// }
///
/// builder.Services
///     .AddHttpContextAccessor()
///     .AddAuthorizingMediator&lt;HttpContextAuthorizationProvider&gt;();
/// </code>
/// </example>
public interface IAuthorizationProvider
{
    /// <summary>
    /// Check whether the current caller holds <paramref name="role"/>.
    /// </summary>
    /// <param name="role">The role name to check.</param>
    /// <param name="cancellationToken">Propagated by the caller.</param>
    /// <returns><c>true</c> if the caller is in the role; <c>false</c> otherwise.</returns>
    Task<bool> IsInRoleAsync(string role, CancellationToken cancellationToken = default);
}
