using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.Authorization;
using Stratara.Identity.AspNetCore.Authorization;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI extensions that surface the Stratara permission catalog as ASP.NET Core authorization
/// policies, so HTTP endpoints gate on the same permission vocabulary as the mediator and the
/// outbox dispatcher.
/// </summary>
public static class PermissionPolicyServiceCollectionExtensions
{
    /// <summary>
    /// Register the permission policy provider and handler: every permission declared in the
    /// registered <see cref="PermissionCatalog"/> becomes an on-demand authorization policy —
    /// <c>[Authorize("sims.read")]</c> / <c>.RequireAuthorization("sims.read")</c> — evaluated
    /// through the registered <see cref="IPermissionResolver"/>. Undeclared policy names defer
    /// to the default provider. Requires a registered catalog and resolver (for example
    /// <c>AddPermissionCatalog(...)</c> + <c>AddCatalogPermissionResolver&lt;TUser&gt;()</c>).
    /// </summary>
    /// <remarks>
    /// The policy provider is registered via <c>TryAdd</c> — a host that ships its own
    /// <see cref="IAuthorizationPolicyProvider"/> keeps it and composes the permission lookup
    /// itself.
    /// </remarks>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Turns every declared catalog permission into an on-demand authorization policy:
    /// <code>
    /// services.AddPermissionCatalog(c =&gt; c.Add("sims.read"));
    /// services.AddStrataraPermissionPolicies();
    /// // then: [Authorize("sims.read")]
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraPermissionPolicies(this IServiceCollection services)
    {
        services.TryAddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        return services;
    }
}
