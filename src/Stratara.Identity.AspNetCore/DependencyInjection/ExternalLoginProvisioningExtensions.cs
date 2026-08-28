using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Identity.AspNetCore.Authentication;
using Stratara.Identity.AspNetCore.Services;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI extensions for hardened JIT external-login provisioning — creating or linking a local
/// ASP.NET Identity account on a user's first OpenID Connect sign-in.
/// </summary>
public static class ExternalLoginProvisioningExtensions
{
    /// <summary>
    /// Register <see cref="ExternalLoginProvisioningService{TUser}"/> and its options. Call the
    /// service from your external-sign-in callback with the result of
    /// <c>SignInManager.GetExternalLoginInfoAsync()</c>. The security invariants (verified-email
    /// linking, fail-closed behavior) are on by default; use <paramref name="configure"/> only to
    /// relax them deliberately or to add an invitation gate.
    /// </summary>
    /// <typeparam name="TUser">The host's ASP.NET Identity user entity.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <param name="configure">Optional callback to configure the provisioning options.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <c>null</c>.</exception>
    /// <example>
    /// Creates or links the local account on a first external sign-in, fail-closed — an unverified
    /// external email does not link to an existing account:
    /// <code>
    /// services.AddStrataraExternalLoginProvisioning&lt;ApplicationUser&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraExternalLoginProvisioning<TUser>(
        this IServiceCollection services,
        Action<ExternalLoginProvisioningOptions>? configure = null)
        where TUser : IdentityUser, new()
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddScoped<ExternalLoginProvisioningService<TUser>>();
        return services;
    }
}
