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
    /// command/query types may carry <c>[RequireRole("...")]</c> or
    /// <c>[RequirePermission("...")]</c> guards. Multiple attributes of either kind on the same
    /// request are ANDed — every role and every permission must match. Permission enforcement
    /// additionally requires a registered <see cref="IPermissionResolver"/> and an ambient
    /// session (<c>ISessionContextProvider</c>), both resolved optionally so role-only hosts are
    /// unaffected; a permission-guarded type without a resolver fails fast at startup.
    /// </para>
    /// <para>
    /// The registered <see cref="IMediator"/> instance implements
    /// <see cref="IAuthorizingMediator"/>; the startup-time validator wired by
    /// <see cref="MediatorServiceCollectionExtensions.AddMediator"/> recognises this marker and
    /// accepts the configuration. If you further wrap this mediator with a custom decorator,
    /// have the outermost decorator also implement <see cref="IAuthorizingMediator"/>.
    /// </para>
    /// <para>
    /// As with <see cref="MediatorServiceCollectionExtensions.AddMediator"/>, no telemetry
    /// registration is required: a host-supplied <c>Tracer</c> is used where present, and the
    /// framework's activity source carries the dispatch spans otherwise.
    /// </para>
    /// </remarks>
    /// <typeparam name="TAuthorizationProvider">The concrete provider, e.g. a service that reads roles from <c>HttpContext.User</c>.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Without this, a host that declares <c>[RequirePermission]</c> fails at start-up rather than
    /// letting a guarded request through unchecked:
    /// <code>
    /// services.AddMediator();
    /// services.AddAuthorizingMediator&lt;MembershipAuthorizationProvider&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddAuthorizingMediator<TAuthorizationProvider>(this IServiceCollection services)
        where TAuthorizationProvider : class, IAuthorizationProvider
    {
        services.AddScoped<IAuthorizationProvider, TAuthorizationProvider>();
        services.AddDispatchTracer();
        services.AddScoped<Mediator>();
        services.AddScoped<IMediator>(sp =>
            new AuthorizingMediator(
                sp.GetRequiredService<Mediator>(),
                sp.GetRequiredService<IAuthorizationProvider>(),
                sp.GetService<IPermissionResolver>(),
                sp.GetService<Stratara.Abstractions.Session.ISessionContextProvider>()));

        return services;
    }
}
