using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Stratara.Outbox.RabbitMQ.Outbox;
using Stratara.Outbox.RabbitMQ.Projections;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Projections;
using Stratara.Shared.Diagnostics.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Service-collection extensions for the Stratara outbox + projection-replay stack.</summary>
public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ICommandOutboxDispatcher"/> and <see cref="IEventBundleOutboxDispatcher"/> (scoped) and,
    /// transitively, <see cref="IProjectionReplayState"/> (singleton via <see cref="AddProjectionReplayState"/>).
    /// </summary>
    /// <remarks>
    /// The transitive <see cref="IProjectionReplayState"/> registration is intentional: the outbox dispatchers
    /// consult it before each publish to skip the fast-path while a projection replay is in progress. The
    /// underlying registration uses <c>TryAddSingleton</c>, so it is safe to call this method together with
    /// <see cref="AddProjectionReplayState"/> or <c>AddEventProjectionWorkerServices()</c> — duplicates collapse.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddMessaging();
    /// builder.Services.AddOutboxDispatcher();
    /// </code>
    /// </example>
    public static IServiceCollection AddOutboxDispatcher(this IServiceCollection services)
    {
        services.AddProjectionReplayState();
        services.AddScoped<ICommandOutboxDispatcher, CommandOutboxDispatcher>();
        services.AddScoped<IEventBundleOutboxDispatcher, EventBundleOutboxDispatcher>();
        return services;
    }

    /// <summary>
    /// Registers the singleton <see cref="IProjectionReplayState"/>: Redis-backed where an
    /// <see cref="IConnectionMultiplexer"/> is registered, held in process otherwise. Idempotent
    /// (<c>TryAddSingleton</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The choice is made when the state is first resolved, not when this method runs, so the order
    /// of <c>AddCaching()</c> and the composites does not matter. With a Redis connection the replay
    /// marking, its progress and the replay-request channel are shared by every host on that
    /// connection, and a replay requested in one suppresses publication in all of them. Without one
    /// they live in this process only — a replay requested here suppresses publication here only —
    /// and the host records that once at start-up as a warning (event <c>104_012</c>). A host that
    /// registers its own <see cref="IProjectionReplayState"/> keeps it.
    /// </para>
    /// <para>
    /// Also registers <see cref="ProjectionReplayOptions"/> with its defaults, so the replay marking is
    /// leased even when the consumer configures nothing. Bind the section with
    /// <c>services.Configure&lt;ProjectionReplayOptions&gt;(...)</c> to override the lease.
    /// </para>
    /// </remarks>
    /// <example>
    /// One host needs no Redis; a deployment whose replay must reach several hosts registers the
    /// shared connection, in either order:
    /// <code>
    /// services.AddProjectionReplayState();
    /// builder.AddCaching();                       // optional: makes the replay state span hosts
    /// services.Configure&lt;ProjectionReplayOptions&gt;(o =&gt; o.LeaseSeconds = 600);
    /// </code>
    /// </example>
    public static IServiceCollection AddProjectionReplayState(this IServiceCollection services)
    {
        services.AddOptions<ProjectionReplayOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IProjectionReplayState>(CreateProjectionReplayState);
        return services;
    }

    private static IProjectionReplayState CreateProjectionReplayState(IServiceProvider serviceProvider)
    {
        var options = serviceProvider.GetRequiredService<IOptions<ProjectionReplayOptions>>();
        var redis = serviceProvider.GetService<IConnectionMultiplexer>();
        if (redis is not null)
        {
            return new ProjectionReplayState(redis, options);
        }

        serviceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger<InProcessProjectionReplayState>()
            .LogProjectionReplayCoordinationInProcess();

        return new InProcessProjectionReplayState(options, serviceProvider.GetRequiredService<TimeProvider>());
    }

    /// <summary>Registers the <see cref="OutboxWorker"/> hosted service and binds <see cref="OutboxOptions"/> from configuration.</summary>
    /// <remarks>
    /// Each polling cycle is guarded by <see cref="IOutboxLock"/>. The default registration is
    /// <see cref="NullOutboxLock"/>, a no-op that always grants the lock — safe only for
    /// single-instance deployments. For multi-replica setups call <see cref="AddRedisOutboxLock"/>
    /// afterwards, which overrides the no-op with a Redis-leased lock that lets only one replica
    /// drain at a time.
    /// </remarks>
    /// <example>
    /// Binds <c>OutboxOptions</c> from the <c>Outbox</c> section. A cycle takes one batch of each kind,
    /// so <c>BatchSize</c> and <c>PollingIntervalSeconds</c> together set the drain rate:
    /// <code>
    /// // appsettings.json: { "Outbox": { "BatchSize": 10000, "PollingIntervalSeconds": 30 } }
    /// services.AddOutboxWorker(configuration);
    /// </code>
    /// </example>
    public static IServiceCollection AddOutboxWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName));
        services.TryAddSingleton<IOutboxLock, NullOutboxLock>();
        services.AddHostedService<OutboxWorker>();
        return services;
    }

    /// <summary>
    /// Replaces the default <see cref="NullOutboxLock"/> with the Redis-backed
    /// <see cref="RedisOutboxLock"/>, enabling safe multi-instance outbox-worker deployments.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="StackExchange.Redis.IConnectionMultiplexer"/> to be registered (for
    /// example via <c>AddCaching()</c> from <c>Stratara.Infrastructure</c>). The lock is leased
    /// with <see cref="OutboxOptions.LockLeaseSeconds"/>; tune the lease to comfortably exceed the
    /// worst-case drain duration.
    /// </remarks>
    /// <example>
    /// Required before running more than one outbox-worker replica; the default lock is a no-op that
    /// assumes a single instance. Needs an <c>IConnectionMultiplexer</c>, which <c>AddCaching()</c>
    /// registers:
    /// <code>
    /// builder.AddCaching();
    /// builder.Services.AddRedisOutboxLock();
    /// </code>
    /// </example>
    public static IServiceCollection AddRedisOutboxLock(this IServiceCollection services)
    {
        services.RemoveAll<IOutboxLock>();
        services.AddSingleton<IOutboxLock, RedisOutboxLock>();
        return services;
    }
}