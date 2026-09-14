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
    /// Binds <c>EventSourcingOptions</c> from the <c>EventSourcing</c> configuration section. The
    /// options carry no settings today; the section is reserved for write-side settings. How often an
    /// aggregate is snapshotted is not configuration: it is the registered <c>ISnapshotStrategy</c>.
    /// </summary>
    /// <param name="services">The service collection to register options on.</param>
    /// <param name="configuration">The host configuration providing the options section.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddWriteStore(configuration);
    ///
    /// // Snapshot cadence is a strategy, not a key: every 100 events instead of the default 50.
    /// services.AddSingleton&lt;ISnapshotStrategy&gt;(new VersionThresholdSnapshotStrategy(threshold: 100));
    /// </code>
    /// </example>
    public static IServiceCollection AddWriteStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EventSourcingOptions>()
            .Bind(configuration.GetSection(EventSourcingOptions.SectionName));

        return services;
    }
}
