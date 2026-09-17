using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Orleans.Aggregates;
using Stratara.Orleans.Hosting;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the aggregate grain on the mediator pipeline.</summary>
public static class OrleansAggregateServiceCollectionExtensions
{
    /// <summary>
    /// Forwards every command that names an aggregate into that aggregate's grain, where the
    /// command's handler runs in the grain's turn. Call it after every other pipeline behaviour is
    /// registered: the behaviour it adds must be the innermost one. The silo publishes the command role:
    /// aggregates, commands that name no aggregate and heavy work are placed only on silos that called this,
    /// so call it where the command handlers are — every handler of the role, on every silo that registers it.
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
        if (!services.Any(d => d.ImplementationType == typeof(AggregateGrainBehavior<>)))
        {
            services.AddTransient(typeof(IPipelineBehavior<>), typeof(AggregateGrainBehavior<>));
        }

        AddIntentCompletion(services);
        RolePlacement.Publish(services, ExecutionRole.Commands);
        DurableDirectoryCheck.Register(services);
        return services;
    }

    /// <summary>
    /// Every silo that can host an aggregate grain completes intents and renews their hand-over,
    /// because placement decides where a recorded intent runs, not the host that recorded it.
    /// </summary>
    private static void AddIntentCompletion(IServiceCollection services)
    {
        services.AddOptions<OrleansDispatchOptions>();
        OrleansOptionsValidator.Register<OrleansDispatchOptions>(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IntentCompletionQueue>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, IntentCompletionQueue>(sp => sp.GetRequiredService<IntentCompletionQueue>()));
    }

    /// <summary>
    /// Replaces the bus-backed <see cref="Stratara.Abstractions.Outbox.ICommandOutboxDispatcher"/>
    /// with the durable-intent one: commands are recorded before the dispatch returns and handed to
    /// their grain, and the outbox drain resumes any hand-off that was lost, up to the
    /// <see cref="MessageRetryOptions.MaxDeliveryAttempts"/> the host configures for bus messages,
    /// after which the command is kept for an operator. Binds no configuration of its own. Requires an
    /// <see cref="Stratara.Abstractions.Outbox.ICommandIntentStore"/> — for example
    /// <c>AddStrataraIntentStore&lt;TWriteContext&gt;()</c> — and the host fails at start without one.
    /// Register it after the composite that registered the bus dispatcher, and register
    /// <c>OutboxDrainWork</c> as singleton work so something resumes the commands. It takes the undecorated
    /// dispatcher slot, so <c>AddAuthorizingCommandOutboxDispatcher</c> authorizes every enqueue whether it is
    /// registered before or after this call. Each command runs through the mediator pipeline in its grain.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.AddBackendServices();
    /// builder.Services
    ///     .AddStrataraOrleansCommandDispatcher()
    ///     .AddStrataraIntentStore&lt;AppWriteDbContext&gt;()
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

        services.AddOptions<MessageRetryOptions>();
        services.TryAddScoped<AggregateSendLane>();
        services.TryAddScoped<IntentRecorder>();
        services.TryAddScoped<IntentHandOver>();
        services.TryAddScoped<IntentResumer>();
        services.TryAddSingleton<ReplaySuspensionTracker>();
        services.AddOptions<HeavyWorkOptions>();
        OrleansOptionsValidator.Register<HeavyWorkOptions>(services);
        services.TryAddScoped<OrleansCommandDispatcher>();
        ReplaceUndecoratedDispatcher(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, IntentStoreStartupCheck>());
        Stratara.Orleans.Hosting.DurableDirectoryCheck.Register(services);
        AddIntentCompletion(services);
        return services;
    }

    /// <summary>
    /// Takes the undecorated dispatcher slot: the one an enqueue-time decorator registered under the key
    /// <c>typeof(ICommandOutboxDispatcher)</c> when there is one, so the decorator keeps wrapping this
    /// dispatcher, and otherwise the last plain registration. A decorator registered after this call wraps
    /// this dispatcher in turn.
    /// </summary>
    private static void ReplaceUndecoratedDispatcher(IServiceCollection services)
    {
        var slot = typeof(Stratara.Abstractions.Outbox.ICommandOutboxDispatcher);
        var decorated = services.LastOrDefault(d => d.ServiceType == slot && d.IsKeyedService && Equals(d.ServiceKey, slot));
        if (decorated is not null)
        {
            services.Remove(decorated);
            services.AddKeyedScoped<Stratara.Abstractions.Outbox.ICommandOutboxDispatcher>(slot, (sp, _) => sp.GetRequiredService<OrleansCommandDispatcher>());
            return;
        }

        var plain = services.LastOrDefault(d => d.ServiceType == slot && !d.IsKeyedService);
        if (plain is not null)
        {
            services.Remove(plain);
        }

        services.AddScoped<Stratara.Abstractions.Outbox.ICommandOutboxDispatcher>(sp => sp.GetRequiredService<OrleansCommandDispatcher>());
    }

    /// <summary>
    /// Settings for heavy work: the cluster-wide limit, the permit retry and the permit lease. Binds no
    /// configuration; the settings are validated when the host starts. Heavy commands reach the pool through
    /// <c>AddStrataraOrleansCommandDispatcher()</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The settings.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddStrataraOrleansCommandDispatcher()
    ///     .ConfigureStrataraHeavyWork(options => options.ClusterWideLimit = 4);
    /// </code>
    /// </example>
    public static IServiceCollection ConfigureStrataraHeavyWork(this IServiceCollection services, Action<HeavyWorkOptions> configure)
    {
        services.AddOptions<HeavyWorkOptions>().Configure(configure);
        OrleansOptionsValidator.Register<HeavyWorkOptions>(services);
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
