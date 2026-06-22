using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Infrastructure.EventSourcing;
using Stratara.Abstractions.EventSourcing;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Service-collection extensions for the Stratara event-sourcing runtime and the event-stream hashing worker.</summary>
public static class EventSourcingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core event-sourcing services as scoped: <see cref="IEventSource"/>,
    /// <see cref="IAggregationService"/>, <see cref="IChangeSetHandler"/>, <see cref="IEventTypeResolver"/>,
    /// and <see cref="ISnapshotService"/>. Also contributes the default
    /// <see cref="VersionThresholdSnapshotStrategy"/> via <c>TryAddSingleton</c>.
    /// </summary>
    /// <remarks>
    /// To control the snapshot cadence — vary it per aggregate type, change the threshold, or
    /// disable snapshots with <see cref="NoSnapshotStrategy"/> — register your own singleton
    /// <see cref="ISnapshotStrategy"/>. A custom registration overrides the default.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddEventSourcing(this IServiceCollection services)
    {
        services.AddScoped<IEventSource, EventSource>();
        services.AddScoped<IAggregationService, AggregationService>();
        services.AddScoped<IChangeSetHandler, ChangeSetHandler>();
        services.AddScoped<IEventTypeResolver, EventTypeResolver>();
        services.AddScoped<ISnapshotService, SnapshotService>();
        services.TryAddSingleton<ISnapshotStrategy, VersionThresholdSnapshotStrategy>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="EventStreamHashWorker"/> as a hosted service and the supporting
    /// <see cref="IEventStreamHashService"/> / <see cref="IEventChainService"/> implementations.
    /// </summary>
    /// <remarks>
    /// Only one host across the deployment should run the hash worker. The worker computes SHA-256
    /// chain hashes over committed events and writes periodic anchors; running it on multiple hosts
    /// can produce duplicate anchors and hash-update conflicts.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddEventStreamHashWorker(this IServiceCollection services)
    {
        services.AddHostedService<EventStreamHashWorker>();
        services.AddScoped<IEventStreamHashService, EventStreamHashService>();
        services.AddScoped<IEventChainService, EventChainService>();
        return services;
    }
}
