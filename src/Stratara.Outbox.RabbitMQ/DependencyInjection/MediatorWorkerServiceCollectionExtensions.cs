using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Outbox.RabbitMQ.Mediator;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Service-collection extensions for the Stratara mediator-command worker.</summary>
public static class MediatorWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the interactive <see cref="MediatorCommandWorker"/> as a hosted service. The worker
    /// subscribes to the configured command topic, deserializes incoming <c>CommandEnvelope</c>s,
    /// restores the session context, and dispatches them through <c>IMediator</c>. Commands marked
    /// <see cref="Stratara.Abstractions.Mediator.IHeavyCommand"/> are routed to a separate topic and
    /// are NOT drained by this lane — run <see cref="AddHeavyCommandWorker"/> to process them.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddMediatorWorker(this IServiceCollection services)
    {
        services.AddHostedService<MediatorCommandWorker>();
        return services;
    }

    /// <summary>
    /// Registers a dedicated heavy-command <see cref="MediatorCommandWorker"/> lane as a hosted
    /// service. It drains the heavy-command topic/subscription (the lane that commands marked
    /// <see cref="Stratara.Abstractions.Mediator.IHeavyCommand"/> are dispatched to), keeping the
    /// interactive lane permanently free. Run it in the same host as <see cref="AddMediatorWorker"/>
    /// (two lanes, one process) or in a dedicated, independently scaled heavy-command host.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="degreeOfParallelism">
    /// The number of concurrent subscriptions this lane opens; <see langword="null"/> defaults to
    /// <see cref="System.Environment.ProcessorCount"/>. Cap it low (e.g. 1–2) to bound the resources
    /// long-running commands can consume.
    /// </param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddHeavyCommandWorker(this IServiceCollection services, int? degreeOfParallelism = null)
    {
        var lane = new CommandWorkerLane(Heavy: true, degreeOfParallelism);
        services.AddSingleton<IHostedService>(sp => ActivatorUtilities.CreateInstance<MediatorCommandWorker>(sp, lane));
        return services;
    }
}
