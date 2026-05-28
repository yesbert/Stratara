using Microsoft.Extensions.DependencyInjection;
using Stratara.Outbox.RabbitMQ.Mediator;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Service-collection extensions for the Stratara mediator-command worker.</summary>
public static class MediatorWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="MediatorCommandWorker"/> as a hosted service. The worker subscribes to
    /// the configured command topic, deserializes incoming <c>CommandEnvelope</c>s, restores the
    /// session context, and dispatches them through <c>IMediator</c>.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddMediatorWorker(this IServiceCollection services)
    {
        services.AddHostedService<MediatorCommandWorker>();
        return services;
    }
}
