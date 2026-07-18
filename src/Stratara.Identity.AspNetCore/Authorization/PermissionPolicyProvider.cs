using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Authorization;

namespace Stratara.Identity.AspNetCore.Authorization;

/// <summary>
/// <see cref="IAuthorizationPolicyProvider"/> that turns every permission declared in the
/// application's <see cref="PermissionCatalog"/> into an authorization policy on demand — so
/// plain <c>[Authorize("sims.read")]</c> (or <c>.RequireAuthorization("sims.read")</c>) works
/// without registering one policy per permission by hand.
/// </summary>
/// <remarks>
/// Policy names that are not declared in the catalog defer to the default provider, so
/// conventional named policies keep working unchanged. Generated policies require an
/// authenticated user plus the <see cref="PermissionRequirement"/>, evaluated by
/// <see cref="PermissionAuthorizationHandler"/> against the registered
/// <see cref="IPermissionResolver"/>.
/// </remarks>
/// <param name="options">The host's authorization options (consumed by the fallback default provider).</param>
/// <param name="catalog">The application's permission catalog.</param>
public sealed class PermissionPolicyProvider(
    IOptions<AuthorizationOptions> options,
    PermissionCatalog catalog) : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    /// <inheritdoc/>
    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!catalog.Contains(policyName))
        {
            return _fallback.GetPolicyAsync(policyName);
        }

        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(policyName))
            .Build();

        return Task.FromResult<AuthorizationPolicy?>(policy);
    }

    /// <inheritdoc/>
    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    /// <inheritdoc/>
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();
}
