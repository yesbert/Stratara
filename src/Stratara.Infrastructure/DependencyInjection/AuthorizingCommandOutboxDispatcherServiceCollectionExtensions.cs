using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Infrastructure.Authorization;
using Stratara.Outbox.RabbitMQ.Outbox;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Authorization;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service-collection extensions that wrap the registered <see cref="ICommandOutboxDispatcher"/>
/// with <see cref="AuthorizingCommandOutboxDispatcher"/>, enforcing <see cref="RequireRoleAttribute"/>
/// and <see cref="RequirePermissionAttribute"/> checks on every enqueued command (permission
/// enforcement activates when an <see cref="IPermissionResolver"/> is registered).
/// </summary>
public static class AuthorizingCommandOutboxDispatcherServiceCollectionExtensions
{
    /// <summary>The key of the dispatcher the authorizer decorates.</summary>
    private static readonly Type DecoratedSlot = typeof(ICommandOutboxDispatcher);

    /// <summary>
    /// Registers <see cref="AuthorizingCommandOutboxDispatcher"/> as the <see cref="ICommandOutboxDispatcher"/>
    /// (scoped), decorating whichever dispatcher was registered last — or <see cref="CommandOutboxDispatcher"/>
    /// when none was — and keeping <see cref="CommandOutboxDispatcher"/> available for direct resolution.
    /// </summary>
    /// <remarks>
    /// The decorated dispatcher moves to a keyed registration under the key
    /// <c>typeof(ICommandOutboxDispatcher)</c>. A dispatcher registration made after this call that replaces
    /// that keyed slot, as the Orleans execution model's does, stays authorized, so the two compose in
    /// either order. Calling this twice decorates once.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// Register it after the dispatcher it decorates, so the outbox path enforces the same guards the
    /// mediator does:
    /// <code>
    /// services.AddOutboxDispatcher();
    /// services.AddAuthorizingCommandOutboxDispatcher();
    /// </code>
    /// </example>
    public static IServiceCollection AddAuthorizingCommandOutboxDispatcher(this IServiceCollection services)
    {
        services.TryAddScoped<CommandOutboxDispatcher>();
        if (services.Any(IsDecoratedSlot))
        {
            return services;
        }

        var existing = services.LastOrDefault(d => d.ServiceType == typeof(ICommandOutboxDispatcher) && !d.IsKeyedService);
        if (existing is null)
        {
            services.AddKeyedScoped<ICommandOutboxDispatcher>(DecoratedSlot, (sp, _) => sp.GetRequiredService<CommandOutboxDispatcher>());
        }
        else
        {
            services.Remove(existing);
            services.Add(ToDecoratedSlot(existing));
        }

        services.AddScoped<ICommandOutboxDispatcher>(sp =>
            new AuthorizingCommandOutboxDispatcher(
                sp.GetRequiredKeyedService<ICommandOutboxDispatcher>(DecoratedSlot),
                sp.GetRequiredService<IAuthorizationProvider>(),
                sp.GetService<IPermissionResolver>(),
                sp.GetService<Stratara.Abstractions.Session.ISessionContextProvider>()));

        return services;
    }

    private static bool IsDecoratedSlot(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(ICommandOutboxDispatcher) && descriptor.IsKeyedService && Equals(descriptor.ServiceKey, DecoratedSlot);

    private static ServiceDescriptor ToDecoratedSlot(ServiceDescriptor existing)
    {
        if (existing.ImplementationInstance is { } instance)
        {
            return new ServiceDescriptor(typeof(ICommandOutboxDispatcher), DecoratedSlot, instance);
        }

        if (existing.ImplementationFactory is { } factory)
        {
            return new ServiceDescriptor(typeof(ICommandOutboxDispatcher), DecoratedSlot, (sp, _) => factory(sp), existing.Lifetime);
        }

        return new ServiceDescriptor(typeof(ICommandOutboxDispatcher), DecoratedSlot, existing.ImplementationType!, existing.Lifetime);
    }
}
