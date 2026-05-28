using Microsoft.Extensions.DependencyInjection;
using Stratara.Infrastructure.Multitenancy;
using Stratara.Abstractions.Multitenancy;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service-collection extensions for the session-backed identity adapters
/// (<see cref="ITenantService"/> and <see cref="ICurrentUserService"/>).
/// </summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scoped <see cref="ITenantService"/> and <see cref="ICurrentUserService"/> implementations
    /// that resolve their values from the ambient <see cref="Stratara.Abstractions.Session.ISessionContextProvider"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddIdentity(this IServiceCollection services)
    {
        services.AddScoped<ITenantService, TenantService>();
        services.AddScoped<ICurrentUserService, CurrentUserService>();

        return services;
    }
}
