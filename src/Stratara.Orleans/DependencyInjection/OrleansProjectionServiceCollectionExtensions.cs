using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Outbox;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Projections;
using Stratara.Orleans.Sagas;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the store-reading grains: projections and sagas.</summary>
public static class OrleansProjectionServiceCollectionExtensions
{
    /// <summary>
    /// Runs every registered projection in grains that read the store in commit order from a
    /// checkpoint kept in <typeparamref name="TReadContext"/>, and turns the bundle dispatcher into
    /// the wake-up hint. The host registers the <c>ICommittedPositionReader</c> of its choice and
    /// applies <c>ProjectionCheckpointModel</c> to its read context. Call it after the projection
    /// composite: the bus-fed projection worker and the full-replay worker it registered are removed.
    /// </summary>
    /// <typeparam name="TReadContext">The read context holding the checkpoint table.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional settings.</param>
    /// <param name="hybrid">
    /// Whether the bundle is still published through the dispatcher registered before this call.
    /// Off, the bus never sees bundles; on, both paths run and the projections still read the store.
    /// </param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.AddEventProjectionWorkerServices();
    /// builder.Services
    ///     .AddProjectionsFromAssemblyContaining&lt;IAppMarker&gt;()
    ///     .AddSingleton&lt;ICommittedPositionReader, PostgresTransactionIdReader&lt;AppWriteDbContext&gt;&gt;()
    ///     .AddStrataraProjectionGrains&lt;AppReadDbContext&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraProjectionGrains<TReadContext>(
        this IServiceCollection services,
        Action<ProjectionGrainOptions>? configure = null,
        bool hybrid = false)
        where TReadContext : DbContext
    {
        var options = services.AddOptions<ProjectionGrainOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        AddStoreReaderCore<TReadContext>(services, hybrid);
        services.AddScoped<INudgeTarget, ProjectionNudgeTarget>();
        services.TryAddSingleton<IProjectionRebuilder, ProjectionRebuilder>();
        if (hybrid)
        {
            RemoveHostedServices(services, "Stratara.Projections.Services.ProjectionReplayWorker");
        }
        else
        {
            RemoveHostedServices(services, "Stratara.Projections.Services.ProjectionWorker", "Stratara.Projections.Services.ProjectionReplayWorker");
        }

        return services;
    }

    /// <summary>
    /// Runs every registered saga in a grain per partition that reads the store in commit order from
    /// a checkpoint kept in <typeparamref name="TReadContext"/>. Existing sagas run unchanged. Call
    /// it after the saga composite: the bus-fed saga worker it registered is removed.
    /// </summary>
    /// <typeparam name="TReadContext">The read context holding the checkpoint table.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional settings.</param>
    /// <param name="hybrid">Whether bundles are still published through the previously registered dispatcher.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.AddSagaWorkerServices();
    /// builder.Services
    ///     .AddSagasFromAssemblyContaining&lt;IAppMarker&gt;()
    ///     .AddSingleton&lt;ICommittedPositionReader, PostgresTransactionIdReader&lt;AppWriteDbContext&gt;&gt;()
    ///     .AddStrataraSagaGrains&lt;AppReadDbContext&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraSagaGrains<TReadContext>(
        this IServiceCollection services,
        Action<SagaGrainOptions>? configure = null,
        bool hybrid = false)
        where TReadContext : DbContext
    {
        var options = services.AddOptions<SagaGrainOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        AddStoreReaderCore<TReadContext>(services, hybrid);
        services.AddScoped<INudgeTarget, SagaNudgeTarget>();
        RemoveHostedServices(services, "Stratara.Sagas.Services.SagaWorker");
        AddSagaProcessTimers(services);
        return services;
    }

    /// <summary>
    /// Processes own timers. Their owner check and handler take over the timer port; a host that
    /// registered its own before this call keeps them for every owner that is not a process.
    /// </summary>
    private static void AddSagaProcessTimers(IServiceCollection services)
    {
        var owners = services.LastOrDefault(d => d.ServiceType == typeof(Stratara.Orleans.Timers.ITimerOwners));
        var handler = services.LastOrDefault(d => d.ServiceType == typeof(Stratara.Orleans.Timers.ITimerHandler));
        if (owners is not null && handler is not null && owners.ImplementationType != typeof(SagaProcessTimerHost))
        {
            services.Remove(owners);
            services.Remove(handler);
            services.Add(ServiceDescriptor.Describe(typeof(HostTimerServices),
                sp => new HostTimerServices(
                    (Stratara.Orleans.Timers.ITimerOwners)Instantiate(sp, owners),
                    (Stratara.Orleans.Timers.ITimerHandler)Instantiate(sp, handler)),
                ServiceLifetime.Scoped));
        }

        services.AddStrataraDurableTimers();
        services.AddScoped<SagaProcessTimerHost>();
        services.AddScoped<Stratara.Orleans.Timers.ITimerOwners>(sp => sp.GetRequiredService<SagaProcessTimerHost>());
        services.AddScoped<Stratara.Orleans.Timers.ITimerHandler>(sp => sp.GetRequiredService<SagaProcessTimerHost>());
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

        return ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!);
    }

    private static void AddStoreReaderCore<TReadContext>(IServiceCollection services, bool hybrid) where TReadContext : DbContext
    {
        services.AddOptions<CommitOrderOptions>();
        services.TryAddScoped<IProjectionCheckpointStore, ProjectionCheckpointStore<TReadContext>>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, StoreReaderGrainStarter>());
        if (!services.Any(d => d.ServiceType == typeof(OrleansEventBundleDispatcher)))
        {
            ReplaceBundleDispatcher(services, hybrid);
        }
    }

    /// <summary>
    /// The store-reading grains take the same composite registration a bus host uses and remove the
    /// bus-fed workers it registered, by name, because those services are internal and registered in
    /// one call. Splitting that registration is a change to a shipped package — a productisation
    /// item, not a proof-of-concept one.
    /// </summary>
    private static void RemoveHostedServices(IServiceCollection services, params string[] implementationTypeNames)
    {
        foreach (var descriptor in services
                     .Where(d => d.ServiceType == typeof(IHostedService) && implementationTypeNames.Contains(d.ImplementationType?.FullName))
                     .ToList())
        {
            services.Remove(descriptor);
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
