using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.Runtime.Placement;
using Stratara.Abstractions.Singleton;

namespace Stratara.Orleans.Singleton;

/// <summary>Places a singleton work's grain only on a silo that registered the work.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
internal sealed class SingletonWorkPlacementFilterAttribute() : PlacementFilterAttribute(new SingletonWorkPlacementFilterStrategy());

/// <summary>The strategy behind <see cref="SingletonWorkPlacementFilterAttribute"/>; it carries no settings.</summary>
internal sealed class SingletonWorkPlacementFilterStrategy() : PlacementFilterStrategy(order: 0);

/// <summary>
/// Keeps the silos whose metadata names the work the grain runs — its key — so a work registered on some
/// silos is never activated on one that lacks it, where the activation would fail. A silo that publishes no
/// metadata, because it was not registered through <c>AddStrataraOrleans</c>, places the work as the runtime
/// would without the filter. The silo placing is judged by the entries it publishes itself, because its own metadata
/// may not have reached its cache yet while it becomes active.
/// </summary>
internal sealed class SingletonWorkPlacementFilterDirector(IServiceProvider services) : IPlacementFilterDirector
{
    private readonly ISiloMetadataCache? _siloMetadata = services.GetService<ISiloMetadataCache>();
    private readonly SingletonWorkPlacement.SingletonWorkSiloMetadata? _ownMetadata = services.GetService<SingletonWorkPlacement.SingletonWorkSiloMetadata>();
    private readonly SiloAddress? _localSilo = services.GetService<ILocalSiloDetails>()?.SiloAddress;

    public IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos)
    {
        if (_siloMetadata is null || _ownMetadata is null)
        {
            return silos;
        }

        var key = SingletonWorkPlacement.MetadataKeyOf(target.GrainIdentity.Key.ToString());
        return silos.Where(silo => silo.Equals(_localSilo)
            ? _ownMetadata.Entries.ContainsKey(key)
            : _siloMetadata.GetSiloMetadata(silo).Metadata.ContainsKey(key));
    }
}

/// <summary>
/// The placement of singleton work, and the metadata the role placement shares with it. Every silo of the execution
/// model registers the filters, because a grain class that names a filter cannot be placed from a silo that lacks
/// it. A silo registered through <c>AddStrataraOrleans</c> also publishes, in its metadata, every singleton work and
/// every role registered on it — read when the silo builds its metadata, so both may be registered before or after
/// the silo.
/// </summary>
internal static class SingletonWorkPlacement
{
    private const string MetadataPrefix = "stratara.singleton-work.";

    public static string MetadataKeyOf(string workName) => MetadataPrefix + workName;

    /// <summary>Registers the filter; calling it again changes nothing.</summary>
    public static void AddFilter(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(SingletonWorkPlacementFilterStrategy)))
        {
            return;
        }

        services.AddSingleton<SingletonWorkPlacementFilterStrategy>();
        services.AddPlacementFilter<SingletonWorkPlacementFilterStrategy, SingletonWorkPlacementFilterDirector>(ServiceLifetime.Singleton);
        Hosting.RolePlacement.AddFilters(services);
    }

    /// <summary>Publishes the singleton work registered on this silo in its metadata, and registers the filter.</summary>
    public static void Register(ISiloBuilder silo)
    {
        AddFilter(silo.Services);
        if (silo.Services.Any(d => d.ServiceType == typeof(SingletonWorkSiloMetadata)))
        {
            return;
        }

        var metadata = new SingletonWorkSiloMetadata();
        silo.Services.AddSingleton(metadata);

        // Registered before the runtime's own configuration of the metadata, which reads the entries this fills.
        silo.Services.AddOptions<SiloMetadata>().Configure<IServiceScopeFactory>((_, scopeFactory) => metadata.Fill(scopeFactory));
        silo.UseSiloMetadata(metadata.Entries);
    }

    /// <summary>The metadata entries naming the singleton work and the execution-model roles registered on this silo.</summary>
    internal sealed class SingletonWorkSiloMetadata
    {
        public Dictionary<string, string> Entries { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Writes the entries. A work registered with its name is published under that name without being constructed;
        /// a work registered without one is constructed here, while the silo starts, to read its name.
        /// </summary>
        /// <exception cref="InvalidOperationException">A work registered without a name could not be constructed.</exception>
        public void Fill(IServiceScopeFactory scopeFactory)
        {
            using var scope = scopeFactory.CreateScope();
            foreach (var name in PublishedNames(scope.ServiceProvider))
            {
                Entries[MetadataKeyOf(name)] = "registered";
            }

            Hosting.RolePlacement.Fill(Entries, scope.ServiceProvider);
        }

        private static IEnumerable<string> PublishedNames(IServiceProvider services)
        {
            var registrations = services.GetService<SingletonWorkRegistrations>();
            if (registrations is null)
            {
                return services.GetServices<ISingletonWork>().Select(work => work.Name);
            }

            return registrations.Works.Select(work => work.Name ?? Construct(services, work.WorkType).Name);
        }

        private static ISingletonWork Construct(IServiceProvider services, Type workType)
        {
            try
            {
                return (ISingletonWork)ActivatorUtilities.CreateInstance(services, workType);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"The singleton work {workType} was registered without its name, so the silo constructed it while it starts to publish the name it runs under, and the construction failed. " +
                    $"Register it with its name — AddStrataraSingletonWork<{workType.Name}>(name) — and it is first constructed when the silo is active.",
                    exception);
            }
        }
    }
}
