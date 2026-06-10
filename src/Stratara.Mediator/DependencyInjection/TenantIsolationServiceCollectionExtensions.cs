using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.Multitenancy;
using Stratara.Mediator.Multitenancy;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI extensions that register the Stratara tenant-isolation pipeline behavior.
/// </summary>
public static class TenantIsolationServiceCollectionExtensions
{
    /// <summary>
    /// Register the tenant-isolation pipeline behaviors for both request shapes
    /// (<see cref="Stratara.Abstractions.Mediator.IRequest"/> and
    /// <see cref="Stratara.Abstractions.Mediator.IRequest{TResult}"/>). The behavior acts only on
    /// requests that implement <see cref="ITenantScopedRequest"/>; all others pass through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this <b>after</b> <c>AddStrataraValidation()</c> (so validation stays the outermost
    /// behavior) and before registering the handler-adjacent behaviors. A tenant-scoped request is
    /// rejected with <see cref="TenantAccessDeniedException"/> when its <c>TenantId</c> does not match
    /// the session's data-owner tenant.
    /// </para>
    /// <para>
    /// The default <see cref="TenantIsolationMode.Default"/> permits privileged cross-tenant
    /// operations (the calling endpoint promotes the session's data-owner tenant to the target before
    /// dispatch). Opt into <see cref="TenantIsolationMode.Strict"/> to additionally route every
    /// cross-tenant operation through an <see cref="ICrossTenantAuthorizer"/>. Stratara registers a
    /// deny-all authorizer via <c>TryAdd</c>, so register your own <see cref="ICrossTenantAuthorizer"/>
    /// to grant the cross-tenant case (e.g. for a platform administrator).
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to mutate.</param>
    /// <param name="configure">Optional callback to configure the enforcement mode.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Strict isolation with a platform-admin cross-tenant authorizer:
    /// <code>
    /// builder.Services
    ///     .AddStrataraValidation()
    ///     .AddStrataraTenantIsolation(o =&gt; o.Mode = TenantIsolationMode.Strict);
    ///
    /// builder.Services.AddScoped&lt;ICrossTenantAuthorizer, PlatformAdminCrossTenantAuthorizer&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraTenantIsolation(
        this IServiceCollection services,
        Action<TenantIsolationOptions>? configure = null)
    {
        var options = new TenantIsolationOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.TryAddSingleton<ICrossTenantAuthorizer, DenyAllCrossTenantAuthorizer>();

        services.AddPipelineBehavior(typeof(TenantIsolationBehavior<>));
        services.AddPipelineBehaviorWithResult(typeof(TenantIsolationBehavior<,>));
        return services;
    }
}
