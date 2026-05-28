using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Outbox.RabbitMQ.Outbox;
using Stratara.Outbox.RabbitMQ.Projections;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Projections;

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
    public static IServiceCollection AddOutboxDispatcher(this IServiceCollection services)
    {
        services.AddProjectionReplayState();
        services.AddScoped<ICommandOutboxDispatcher, CommandOutboxDispatcher>();
        services.AddScoped<IEventBundleOutboxDispatcher, EventBundleOutboxDispatcher>();
        return services;
    }

    /// <summary>Registers the singleton Redis-backed <see cref="IProjectionReplayState"/>. Idempotent (<c>TryAddSingleton</c>).</summary>
    public static IServiceCollection AddProjectionReplayState(this IServiceCollection services)
    {
        services.TryAddSingleton<IProjectionReplayState, ProjectionReplayState>();
        return services;
    }

    /// <summary>Registers the <see cref="OutboxWorker"/> hosted service and binds <see cref="OutboxOptions"/> from configuration.</summary>
    /// <remarks>
    /// Each polling cycle is guarded by <see cref="IOutboxLock"/>. The default registration is
    /// <see cref="NullOutboxLock"/>, a no-op that always grants the lock — safe only for
    /// single-instance deployments. For multi-replica setups call <see cref="AddRedisOutboxLock"/>
    /// afterwards, which overrides the no-op with a Redis-leased lock that lets only one replica
    /// drain at a time.
    /// </remarks>
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
    public static IServiceCollection AddRedisOutboxLock(this IServiceCollection services)
    {
        services.RemoveAll<IOutboxLock>();
        services.AddSingleton<IOutboxLock, RedisOutboxLock>();
        return services;
    }
}