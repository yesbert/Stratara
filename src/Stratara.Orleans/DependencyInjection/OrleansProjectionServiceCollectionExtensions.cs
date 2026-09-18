using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Timers;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Hosting;
using Stratara.Orleans.Projections;
using Stratara.Orleans.Sagas;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the store-reading grains: projections and sagas.</summary>
public static class OrleansProjectionServiceCollectionExtensions
{
    /// <summary>
    /// Runs every registered projection in grains that read the store in commit order from a
    /// checkpoint, and turns the bundle dispatcher into the wake-up hint. The host registers the
    /// <c>ICommittedPositionReader</c> of its choice and a checkpoint store, for example with
    /// <c>AddStrataraProjectionCheckpoints&lt;TReadContext&gt;()</c>. Call it after
    /// <c>AddEventProjectionServices</c>, which registers the projection runtime and the replay worker without the
    /// bus-fed worker; this call removes nothing. After <c>AddEventProjectionWorkerServices</c> both paths run.
    /// The silo publishes the projection role: the projection grains are placed only on silos that called this,
    /// so register every projection of the role on every silo that registers it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional settings.</param>
    /// <param name="hybrid">
    /// Whether the bundle is still published through the dispatcher registered before this call.
    /// Off, the bus never sees bundles; on, both paths run and the projections still read the store.
    /// </param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.AddEventProjectionServices();
    /// builder.Services
    ///     .AddProjectionsFromAssemblyContaining&lt;IAppMarker&gt;()
    ///     .AddSingleton&lt;ICommittedPositionReader, PostgresTransactionIdReader&lt;AppWriteDbContext&gt;&gt;()
    ///     .AddStrataraProjectionCheckpoints&lt;AppReadDbContext&gt;()
    ///     .AddStrataraProjectionGrains();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraProjectionGrains(
        this IServiceCollection services,
        Action<ProjectionGrainOptions>? configure = null,
        bool hybrid = false)
    {
        var options = services.AddOptions<ProjectionGrainOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        OrleansOptionsValidator.Register<ProjectionGrainOptions>(services);
        AddStoreReaderCore(services, hybrid);
        RolePlacement.Publish(services, ExecutionRole.Projections);
        AddNudgeTarget<ProjectionNudgeTarget>(services);
        services.TryAddSingleton<IProjectionRebuilder, ProjectionRebuilder>();
        ReplayCheckpointResetTruncator.Decorate(services, Instantiate);

        return services;
    }

    /// <summary>
    /// Runs every registered saga in a grain per partition that reads the store in commit order from
    /// a checkpoint. Existing sagas run unchanged. The host registers the <c>ICommittedPositionReader</c>
    /// of its choice and a checkpoint store. Call it after <c>AddSagaServices</c>, which registers the saga
    /// runtime without the bus-fed worker; this call removes nothing. The silo publishes the saga role: the saga
    /// and process grains are placed only on silos that called this, so register every saga and process of the
    /// role on every silo that registers it. Processes own durable timers, so the silo needs a reminder service.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional settings.</param>
    /// <param name="hybrid">Whether bundles are still published through the previously registered dispatcher.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.AddSagaServices();
    /// builder.Services
    ///     .AddSagasFromAssemblyContaining&lt;IAppMarker&gt;()
    ///     .AddSingleton&lt;ICommittedPositionReader, PostgresTransactionIdReader&lt;AppWriteDbContext&gt;&gt;()
    ///     .AddStrataraProjectionCheckpoints&lt;AppReadDbContext&gt;()
    ///     .AddStrataraSagaGrains();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraSagaGrains(
        this IServiceCollection services,
        Action<SagaGrainOptions>? configure = null,
        bool hybrid = false)
    {
        var options = services.AddOptions<SagaGrainOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        OrleansOptionsValidator.Register<SagaGrainOptions>(services);
        AddStoreReaderCore(services, hybrid);
        RolePlacement.Publish(services, ExecutionRole.Sagas);
        AddNudgeTarget<SagaNudgeTarget>(services);
        AddSagaProcessTimers(services);
        return services;
    }

    /// <summary>Adds the role's wake-up target once, so a second registration of the role wakes nothing twice.</summary>
    private static void AddNudgeTarget<TTarget>(IServiceCollection services)
        where TTarget : class, INudgeTarget
    {
        if (!services.Any(d => d.ServiceType == typeof(INudgeTarget) && d.ImplementationType == typeof(TTarget)))
        {
            services.AddScoped<INudgeTarget, TTarget>();
        }
    }

    /// <summary>
    /// Processes own timers under a prefix of their own. The host's owner check and handler serve every
    /// other owner, whether they were registered before or after this call.
    /// </summary>
    private static void AddSagaProcessTimers(IServiceCollection services)
    {
        services.AddStrataraDurableTimers();
        if (services.Any(d => d.ServiceType == typeof(SagaProcessTimerHost)))
        {
            return;
        }

        services.AddScoped<SagaProcessTimerHost>();
        services.AddScoped<ITimerOwners>(sp => sp.GetRequiredService<SagaProcessTimerHost>());
        services.AddScoped<ITimerHandler>(sp => sp.GetRequiredService<SagaProcessTimerHost>());
    }

    private static object Instantiate(IServiceProvider services, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is not null)
        {
            return descriptor.ImplementationInstance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return descriptor.ImplementationFactory(services);
        }

        var implementationType = descriptor.ImplementationType
                                 ?? throw new InvalidOperationException($"The registration of {descriptor.ServiceType} names no implementation.");
        return ActivatorUtilities.CreateInstance(services, implementationType);
    }

    private static void AddStoreReaderCore(IServiceCollection services, bool hybrid)
    {
        services.AddOptions<CommitOrderOptions>();
        OrleansOptionsValidator.Register<CommitOrderOptions>(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILifecycleParticipant<global::Orleans.Runtime.ISiloLifecycle>, StoreReaderGrainStarter>());
        services.TryAddScoped<IStoreReaderSeeding, StoreReaderSeeding>();
        Stratara.Orleans.Hosting.DurableDirectoryCheck.Register(services);
        ReplaceBundleDispatcher(services, hybrid);
    }

    /// <summary>
    /// Replaces the bundle dispatcher with the wake-up hint. With <paramref name="hybrid"/> the dispatcher registered
    /// before is kept inside it — by type, factory or instance, with its lifetime — so bundles still reach the bus.
    /// Every registration that asks for it is answered, whether it is the first store-reading role of the host or a
    /// later one: a later call finds the kept dispatcher already in place, or fails because there is none to keep.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="hybrid"/> is set and no bundle dispatcher is registered to keep.</exception>
    private static void ReplaceBundleDispatcher(IServiceCollection services, bool hybrid)
    {
        var replaced = services.Any(d => d.ServiceType == typeof(OrleansEventBundleDispatcher));
        var kept = services.Any(d => d.ServiceType == typeof(InnerBundleDispatcher));
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IEventBundleOutboxDispatcher) && !d.IsKeyedService);
        if (hybrid && !kept && (existing is null || replaced))
        {
            throw new InvalidOperationException(
                "hybrid: true keeps publishing bundles through the bus dispatcher registered before this call, and none is registered. " +
                "Register one first — AddOutboxDispatcher(), which the worker composites call — or pass hybrid: false to read the store only. " +
                "Where another store-reading role was registered before this call, register the bus dispatcher before that one.");
        }

        if (replaced)
        {
            return;
        }

        if (existing is not null)
        {
            services.Remove(existing);
        }

        if (hybrid && existing is not null)
        {
            Keep(services, existing);
        }

        services.AddScoped<OrleansEventBundleDispatcher>();
        services.AddScoped<IEventBundleOutboxDispatcher>(sp => sp.GetRequiredService<OrleansEventBundleDispatcher>());
    }

    /// <summary>The key the kept dispatcher's own registration carries, so it never collides with the host's.</summary>
    private const string KeptDispatcherKey = "stratara.orleans.kept-bundle-dispatcher";

    /// <summary>
    /// Keeps the dispatcher that was registered before, under its own descriptor's shape. A type-shaped registration
    /// is registered again under a key of the framework's own — so the container builds it and disposes it with the
    /// scope it belongs to, without touching a registration of that type the host made for itself; a factory or an
    /// instance is used as it was given, because its owner is whoever supplied it.
    /// </summary>
    private static void Keep(IServiceCollection services, ServiceDescriptor existing)
    {
        var lifetime = existing.Lifetime == ServiceLifetime.Singleton ? ServiceLifetime.Singleton : ServiceLifetime.Scoped;
        if (existing is { ImplementationInstance: null, ImplementationFactory: null, ImplementationType: { } implementationType })
        {
            services.Add(new ServiceDescriptor(implementationType, KeptDispatcherKey, implementationType, lifetime));
            services.Add(new ServiceDescriptor(
                typeof(InnerBundleDispatcher),
                sp => new InnerBundleDispatcher((IEventBundleOutboxDispatcher)sp.GetRequiredKeyedService(implementationType, KeptDispatcherKey)),
                lifetime));
            return;
        }

        services.Add(new ServiceDescriptor(
            typeof(InnerBundleDispatcher),
            sp => new InnerBundleDispatcher((IEventBundleOutboxDispatcher)Instantiate(sp, existing)),
            lifetime));
    }
}
