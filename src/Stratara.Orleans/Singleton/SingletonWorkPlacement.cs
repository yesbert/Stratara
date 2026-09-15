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
/// would without the filter.
/// </summary>
internal sealed class SingletonWorkPlacementFilterDirector(IServiceProvider services) : IPlacementFilterDirector
{
    private readonly ISiloMetadataCache? _siloMetadata = services.GetService<ISiloMetadataCache>();
    private readonly bool _publishesWork = services.GetService<SingletonWorkPlacement.SingletonWorkSiloMetadata>() is not null;

    public IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos)
    {
        if (_siloMetadata is null || !_publishesWork)
        {
            return silos;
        }

        var key = SingletonWorkPlacement.MetadataKeyOf(target.GrainIdentity.Key.ToString());
        return silos.Where(silo => _siloMetadata.GetSiloMetadata(silo).Metadata.ContainsKey(key));
    }
}

/// <summary>
/// The placement of singleton work. Every silo of the execution model registers the filter, because a grain
/// class that names a filter cannot be placed from a silo that lacks it. A silo registered through
/// <c>AddStrataraOrleans</c> also publishes, in its metadata, every singleton work registered on it — read when
/// the silo builds its metadata, so the work may be registered before or after the silo.
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

    /// <summary>The metadata entries naming the singleton work registered on this silo.</summary>
    internal sealed class SingletonWorkSiloMetadata
    {
        public Dictionary<string, string> Entries { get; } = new(StringComparer.Ordinal);

        public void Fill(IServiceScopeFactory scopeFactory)
        {
            using var scope = scopeFactory.CreateScope();
            foreach (var work in scope.ServiceProvider.GetServices<ISingletonWork>())
            {
                Entries[MetadataKeyOf(work.Name)] = "registered";
            }
        }
    }
}
