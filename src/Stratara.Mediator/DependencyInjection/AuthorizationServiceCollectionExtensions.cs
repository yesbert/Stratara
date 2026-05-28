using Microsoft.Extensions.DependencyInjection;
using Stratara.Mediator;
using Stratara.Mediator.Authorization;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Authorization;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI extensions that wrap <see cref="IMediator"/> with an authorization decorator.
/// </summary>
public static class AuthorizationServiceCollectionExtensions
{
    /// <summary>
    /// Register <typeparamref name="TAuthorizationProvider"/> as the
    /// <see cref="IAuthorizationProvider"/> implementation and wire <see cref="IMediator"/> to
    /// dispatch through an authorizing decorator that enforces
    /// <c>RequireRoleAttribute</c> annotations on the request type before delegating to the
    /// inner mediator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use this instead of <see cref="MediatorServiceCollectionExtensions.AddMediator"/> when
    /// command/query types may carry <c>[RequireRole("...")]</c> guards. Multiple
    /// <c>[RequireRole]</c> attributes on the same request are ANDed — every role must match.
    /// </para>
    /// <para>
    /// The registered <see cref="IMediator"/> instance implements
    /// <see cref="IAuthorizingMediator"/>; the startup-time validator wired by
    /// <see cref="MediatorServiceCollectionExtensions.AddMediator"/> recognises this marker and
    /// accepts the configuration. If you further wrap this mediator with a custom decorator,
    /// have the outermost decorator also implement <see cref="IAuthorizingMediator"/>.
    /// </para>
    /// </remarks>
    /// <typeparam name="TAuthorizationProvider">The concrete provider, e.g. a service that reads roles from <c>HttpContext.User</c>.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    public static IServiceCollection AddAuthorizingMediator<TAuthorizationProvider>(this IServiceCollection services)
        where TAuthorizationProvider : class, IAuthorizationProvider
    {
        services.AddScoped<IAuthorizationProvider, TAuthorizationProvider>();
        services.AddScoped<Mediator>();
        services.AddScoped<IMediator>(sp =>
            new AuthorizingMediator(
                sp.GetRequiredService<Mediator>(),
                sp.GetRequiredService<IAuthorizationProvider>()));

        return services;
    }
}
