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
/// <remarks>
/// The example is the request-side shape. A command the Orleans execution model records is authorized again where it
/// runs — possibly on another silo, resumed after a crash — from the session recorded with it and without a web request;
/// a host that records commands needs a provider that answers from <c>ISessionContextProvider</c>, such as the
/// membership provider, or every resumed command is refused.
/// </remarks>
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
