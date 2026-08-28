using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Shared.EventSourcing;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service-collection extensions for the Stratara write store.
/// </summary>
public static class WriteStoreServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="EventSourcingOptions"/> from the <c>EventSourcing</c> configuration
    /// section so write-side components (event-stream/snapshot/outbox repositories) can resolve
    /// host-tuned settings such as snapshot cadence and batch sizes.
    /// </summary>
    /// <param name="services">The service collection to register options on.</param>
    /// <param name="configuration">The host configuration providing the options section.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    /// <example>
    /// Binds <c>EventSourcingOptions</c> from the <c>EventSourcing</c> section — the snapshot cadence
    /// and the write-side batch sizes:
    /// <code>
    /// // appsettings.json: { "EventSourcing": { "SnapshotThreshold": 100 } }
    /// services.AddWriteStore(configuration);
    /// </code>
    /// </example>
    public static IServiceCollection AddWriteStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EventSourcingOptions>()
            .Bind(configuration.GetSection(EventSourcingOptions.SectionName));

        return services;
    }
}
