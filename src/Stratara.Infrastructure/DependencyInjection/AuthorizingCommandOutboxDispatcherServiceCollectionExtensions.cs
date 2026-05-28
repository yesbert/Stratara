using Microsoft.Extensions.DependencyInjection;
using Stratara.Infrastructure.Authorization;
using Stratara.Outbox.RabbitMQ.Outbox;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Authorization;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service-collection extensions that wrap the default <see cref="ICommandOutboxDispatcher"/>
/// with <see cref="AuthorizingCommandOutboxDispatcher"/>, enforcing <see cref="RequireRoleAttribute"/>
/// checks on every enqueued command.
/// </summary>
public static class AuthorizingCommandOutboxDispatcherServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AuthorizingCommandOutboxDispatcher"/> as the <see cref="ICommandOutboxDispatcher"/>
    /// (scoped), keeping the inner <see cref="CommandOutboxDispatcher"/> available for direct resolution.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddAuthorizingCommandOutboxDispatcher(this IServiceCollection services)
    {
        services.AddScoped<CommandOutboxDispatcher>();
        services.AddScoped<ICommandOutboxDispatcher>(sp =>
            new AuthorizingCommandOutboxDispatcher(
                sp.GetRequiredService<CommandOutboxDispatcher>(),
                sp.GetRequiredService<IAuthorizationProvider>()));

        return services;
    }
}
