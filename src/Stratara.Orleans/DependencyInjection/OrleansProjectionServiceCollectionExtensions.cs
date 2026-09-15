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
        services.AddScoped<INudgeTarget, ProjectionNudgeTarget>();
        services.TryAddSingleton<IProjectionRebuilder, ProjectionRebuilder>();
        ReplayCheckpointResetTruncator.Decorate(services, Instantiate);

        return services;
    }

    /// <summary>
    /// Runs every registered saga in a grain per partition that reads the store in commit order from
    /// a checkpoint. Existing sagas run unchanged. The host registers the <c>ICommittedPositionReader</c>
    /// of its choice and a checkpoint store. Call it after <c>AddSagaServices</c>, which registers the saga
    /// runtime without the bus-fed worker; this call removes nothing.
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
        services.AddScoped<INudgeTarget, SagaNudgeTarget>();
        AddSagaProcessTimers(services);
        return services;
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
        Stratara.Orleans.Hosting.DurableDirectoryCheck.Register(services);
        if (!services.Any(d => d.ServiceType == typeof(OrleansEventBundleDispatcher)))
        {
            ReplaceBundleDispatcher(services, hybrid);
        }
    }

    private static void ReplaceBundleDispatcher(IServiceCollection services, bool hybrid)
    {
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IEventBundleOutboxDispatcher));
        if (existing is not null)
        {
            services.Remove(existing);
        }

        if (hybrid && existing?.ImplementationType is { } innerType)
        {
            services.AddScoped(innerType);
            services.AddScoped(sp => new InnerBundleDispatcher((IEventBundleOutboxDispatcher)sp.GetRequiredService(innerType)));
        }

        services.AddScoped<OrleansEventBundleDispatcher>();
        services.AddScoped<IEventBundleOutboxDispatcher>(sp => sp.GetRequiredService<OrleansEventBundleDispatcher>());
    }
}
