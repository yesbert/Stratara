using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.Mediator;
using Stratara.Orleans.Aggregates;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the aggregate grain on the mediator pipeline.</summary>
public static class OrleansAggregateServiceCollectionExtensions
{
    /// <summary>
    /// Forwards every command that names an aggregate into that aggregate's grain, where the
    /// command's handler runs in the grain's turn. Call it after every other pipeline behaviour is
    /// registered: the behaviour it adds must be the innermost one.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddMediator()
    ///     .AddCommandHandlersFromAssemblyContaining&lt;IAppMarker&gt;()
    ///     .AddStrataraAggregateGrains();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraAggregateGrains(this IServiceCollection services)
    {
        services.TryAddScoped<AggregateSendLane>();
        services.AddTransient(typeof(IPipelineBehavior<>), typeof(AggregateGrainBehavior<>));
        return services;
    }

    /// <summary>
    /// Replaces the bus-backed <see cref="Stratara.Abstractions.Outbox.ICommandOutboxDispatcher"/>
    /// with the durable-intent one: commands are recorded in the outbox and handed to their grain,
    /// and the outbox drain resumes any hand-off that was lost. Register it after the composite
    /// that registered the bus dispatcher, and register <c>OutboxDrainWork</c> as singleton work so
    /// something resumes them.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.AddBackendServices();
    /// builder.Services
    ///     .AddStrataraOrleansCommandDispatcher()
    ///     .AddStrataraSingletonWork&lt;OutboxDrainWork&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraOrleansCommandDispatcher(this IServiceCollection services, Action<OrleansDispatchOptions>? configure = null)
    {
        var options = services.AddOptions<OrleansDispatchOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<AggregateSendLane>();
        services.AddOptions<HeavyWorkOptions>();
        services.AddScoped<Stratara.Abstractions.Outbox.ICommandOutboxDispatcher, OrleansCommandDispatcher>();
        services.TryAddSingleton<IntentCompletionQueue>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<IntentCompletionQueue>()));
        return services;
    }

    /// <summary>Settings for heavy work: the cluster-wide limit and the permit retry.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The settings.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection ConfigureStrataraHeavyWork(this IServiceCollection services, Action<HeavyWorkOptions> configure)
    {
        services.AddOptions<HeavyWorkOptions>().Configure(configure);
        return services;
    }
}
