using Microsoft.Extensions.DependencyInjection;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.Runtime.Placement;
using Stratara.Abstractions.Timers;

namespace Stratara.Orleans.Hosting;

/// <summary>The roles of the execution model a silo registers, each with the grains that run only where it is registered.</summary>
internal enum ExecutionRole
{
    /// <summary>Aggregate commands, commands that name no aggregate, and heavy work — the silos with the command handlers.</summary>
    Commands,

    /// <summary>Store-reading projections.</summary>
    Projections,

    /// <summary>Store-reading sagas and stateful processes.</summary>
    Sagas,

    /// <summary>Owner-checked durable timers — the silos with a timer owner check and handler.</summary>
    Timers,
}

/// <summary>
/// Placement by role. A silo publishes, in its metadata, the roles its composition registered; the grains of a role
/// carry a placement filter that keeps them on silos publishing it, so a cluster whose silos register different
/// roles places every activation where its handlers, projections, processes or timer ports are. Every silo of the
/// execution model registers every filter, because a grain class that names a filter cannot be placed from a silo
/// that lacks it. The roles are read when the silo builds its metadata, so a role may be registered before or after
/// the silo.
/// </summary>
internal static class RolePlacement
{
    private const string MetadataPrefix = "stratara.role.";

    public static string MetadataKeyOf(ExecutionRole role) => MetadataPrefix + role switch
    {
        ExecutionRole.Commands => "commands",
        ExecutionRole.Projections => "projections",
        ExecutionRole.Sagas => "sagas",
        ExecutionRole.Timers => "timers",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown execution role."),
    };

    /// <summary>The registration that adopts a role, for the message a placement that finds no silo carries.</summary>
    public static string RegistrationOf(ExecutionRole role) => role switch
    {
        ExecutionRole.Commands => "AddStrataraAggregateGrains",
        ExecutionRole.Projections => "AddStrataraProjectionGrains",
        ExecutionRole.Sagas => "AddStrataraSagaGrains",
        ExecutionRole.Timers => "AddStrataraDurableTimers with an ITimerOwners and an ITimerHandler",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown execution role."),
    };

    /// <summary>Records that the composition registers <paramref name="role"/>; the silo publishes it in its metadata.</summary>
    public static void Publish(IServiceCollection services, ExecutionRole role)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(PublishedRoles));
        if (existing?.ImplementationInstance is PublishedRoles roles)
        {
            roles.Add(role);
            return;
        }

        var fresh = new PublishedRoles();
        fresh.Add(role);
        services.AddSingleton(fresh);
    }

    /// <summary>Registers the filter of every role; calling it again changes nothing.</summary>
    public static void AddFilters(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(CommandsRolePlacementFilterStrategy)))
        {
            return;
        }

        services.AddSingleton<CommandsRolePlacementFilterStrategy>();
        services.AddSingleton<ProjectionsRolePlacementFilterStrategy>();
        services.AddSingleton<SagasRolePlacementFilterStrategy>();
        services.AddSingleton<TimersRolePlacementFilterStrategy>();
        services.AddPlacementFilter<CommandsRolePlacementFilterStrategy, RolePlacementFilterDirector>(ServiceLifetime.Singleton);
        services.AddPlacementFilter<ProjectionsRolePlacementFilterStrategy, RolePlacementFilterDirector>(ServiceLifetime.Singleton);
        services.AddPlacementFilter<SagasRolePlacementFilterStrategy, RolePlacementFilterDirector>(ServiceLifetime.Singleton);
        services.AddPlacementFilter<TimersRolePlacementFilterStrategy, RolePlacementFilterDirector>(ServiceLifetime.Singleton);
    }

    /// <summary>
    /// Writes the metadata entries of the roles the composition registers. The timer role needs an owner check to
    /// serve, so it is published only where one is registered — a silo that registers the timers to register them,
    /// with its owners elsewhere, publishes nothing.
    /// </summary>
    public static void Fill(IDictionary<string, string> entries, IServiceProvider services)
    {
        var roles = services.GetService<PublishedRoles>();
        if (roles is null)
        {
            return;
        }

        foreach (var role in roles.Roles)
        {
            if (role == ExecutionRole.Timers && !services.GetServices<ITimerOwners>().Any())
            {
                continue;
            }

            entries[MetadataKeyOf(role)] = "registered";
        }
    }

    /// <summary>
    /// The silos a grain of <paramref name="role"/> may be placed on: those that publish the role. None left is a
    /// cluster in which no silo registered the role, which is reported by name rather than as an empty placement.
    /// </summary>
    /// <exception cref="InvalidOperationException">No silo of the cluster publishes the role.</exception>
    public static IReadOnlyList<SiloAddress> Select(ExecutionRole role, IEnumerable<SiloAddress> silos, Func<SiloAddress, bool> publishes)
    {
        var candidates = silos.Where(publishes).ToList();
        if (candidates.Count > 0)
        {
            return candidates;
        }

        throw new InvalidOperationException(
            $"No silo of the cluster registered the {role.ToString().ToLowerInvariant()} role of the Orleans execution model, so its grain cannot be placed. " +
            $"Register the role on at least one silo with {RegistrationOf(role)}; a silo that registers a role registers every handler, projection, process or timer port of that role.");
    }

    /// <summary>The roles a composition registers, collected across registrations.</summary>
    internal sealed class PublishedRoles
    {
        private readonly HashSet<ExecutionRole> _roles = [];

        public IReadOnlyCollection<ExecutionRole> Roles => _roles;

        public void Add(ExecutionRole role) => _roles.Add(role);
    }
}

/// <summary>A filter that keeps a grain on silos publishing one role; the strategy names the role.</summary>
internal abstract class RolePlacementFilterStrategy(ExecutionRole role) : PlacementFilterStrategy(order: 0)
{
    public ExecutionRole Role { get; } = role;
}

internal sealed class CommandsRolePlacementFilterStrategy() : RolePlacementFilterStrategy(ExecutionRole.Commands);

internal sealed class ProjectionsRolePlacementFilterStrategy() : RolePlacementFilterStrategy(ExecutionRole.Projections);

internal sealed class SagasRolePlacementFilterStrategy() : RolePlacementFilterStrategy(ExecutionRole.Sagas);

internal sealed class TimersRolePlacementFilterStrategy() : RolePlacementFilterStrategy(ExecutionRole.Timers);

/// <summary>Keeps the aggregate, runner and heavy-work grains on silos that registered the command role.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class CommandsRolePlacementFilterAttribute() : PlacementFilterAttribute(new CommandsRolePlacementFilterStrategy());

/// <summary>Keeps the projection grains on silos that registered the projection role.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class ProjectionsRolePlacementFilterAttribute() : PlacementFilterAttribute(new ProjectionsRolePlacementFilterStrategy());

/// <summary>Keeps the saga and process grains on silos that registered the saga role.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class SagasRolePlacementFilterAttribute() : PlacementFilterAttribute(new SagasRolePlacementFilterStrategy());

/// <summary>Keeps the timer owner grains on silos that registered the timer role with its ports.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class TimersRolePlacementFilterAttribute() : PlacementFilterAttribute(new TimersRolePlacementFilterStrategy());

/// <summary>
/// The director behind every role filter. A silo that publishes no roles itself — one registered without
/// <c>AddStrataraOrleans</c> — filters nothing, because it could not see its own registrations either; a silo whose
/// metadata is not yet known is treated as publishing nothing, which delays a placement rather than misplacing it.
/// </summary>
internal sealed class RolePlacementFilterDirector(IServiceProvider services) : IPlacementFilterDirector
{
    private readonly ISiloMetadataCache? _siloMetadata = services.GetService<ISiloMetadataCache>();
    private readonly bool _publishesRoles = services.GetService<Singleton.SingletonWorkPlacement.SingletonWorkSiloMetadata>() is not null;

    public IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos)
    {
        if (_siloMetadata is null || !_publishesRoles || filterStrategy is not RolePlacementFilterStrategy strategy)
        {
            return silos;
        }

        var key = RolePlacement.MetadataKeyOf(strategy.Role);
        return RolePlacement.Select(strategy.Role, silos, silo => _siloMetadata.GetSiloMetadata(silo).Metadata.ContainsKey(key));
    }
}
