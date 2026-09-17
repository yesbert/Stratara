using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;
using Orleans.Runtime;
using Stratara.Orleans.Diagnostics;

namespace Stratara.Orleans.Hosting;

/// <summary>
/// Fails the silo while it initialises when no grain directory is registered under the name the
/// execution model's single-activation grains select — at start, with a message that names the
/// registration, rather than at the first activation that needs it — and when the silo registers a role or singleton
/// work without publishing it, which a silo that registered the directory itself rather than through
/// <c>AddStrataraOrleans</c> does: other silos would place every role's grains on it as if it hosted them all.
/// </summary>
internal sealed class DurableDirectoryCheck(IServiceProvider services, ILogger<DurableDirectoryCheck> logger) : ILifecycleParticipant<ISiloLifecycle>
{
    /// <summary>
    /// Adds the check once, from every registration whose grains need the durable directory — and with it the
    /// singleton work's placement filter, which every silo that can place the model's grains must know.
    /// </summary>
    public static void Register(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILifecycleParticipant<ISiloLifecycle>, DurableDirectoryCheck>());
        Singleton.SingletonWorkPlacement.AddFilter(services);
    }

    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(nameof(DurableDirectoryCheck), ServiceLifecycleStage.RuntimeInitialize, CheckAsync);

    /// <exception cref="InvalidOperationException">
    /// No grain directory is registered under <see cref="GrainDirectories.Durable"/>, or a role or singleton work is
    /// registered and nothing publishes it.
    /// </exception>
    internal Task CheckAsync(CancellationToken cancellationToken)
    {
        if (services.GetKeyedService<IGrainDirectory>(GrainDirectories.Durable) is null)
        {
            logger.LogDirectoryCheckFailed(GrainDirectories.Durable);
            throw new InvalidOperationException(
                $"The Orleans execution model places its single-activation grains in the grain directory '{GrainDirectories.Durable}', and none is registered under that name. Register one with AddStrataraOrleans on the silo builder, for example silo.AddStrataraOrleans((s, name) => s.AddRedisGrainDirectory(name, options => ...)).");
        }

        EnsurePublished();
        return Task.CompletedTask;
    }

    /// <exception cref="InvalidOperationException">A role or singleton work is registered and nothing publishes it.</exception>
    private void EnsurePublished()
    {
        if (services.GetService<Singleton.SingletonWorkPlacement.SingletonWorkSiloMetadata>() is not null)
        {
            return;
        }

        var unpublished = Unpublished(services);
        if (unpublished.Count == 0)
        {
            return;
        }

        var registered = string.Join(", ", unpublished);
        logger.LogRolesUnpublished(registered);
        throw new InvalidOperationException(
            $"The silo registers {registered}, but nothing publishes them to the cluster, so other silos would place their grains here as if this silo hosted every role and work. " +
            "The grain directory was registered without AddStrataraOrleans, which is the call that publishes them: register it with silo.AddStrataraOrleans((s, name) => s.AddRedisGrainDirectory(name, options => ...)) instead.");
    }

    /// <summary>
    /// The roles, by the registration that adopts them, and the singleton works, by their registered names or their
    /// types, that the composition registers. The timer role counts only where an owner check is registered, as it is
    /// published only there.
    /// </summary>
    internal static IReadOnlyList<string> Unpublished(IServiceProvider services)
    {
        var found = new List<string>();
        var isService = services.GetService<IServiceProviderIsService>();
        foreach (var role in services.GetService<RolePlacement.PublishedRoles>()?.Roles ?? [])
        {
            if (role == ExecutionRole.Timers && isService?.IsService(typeof(Stratara.Abstractions.Timers.ITimerOwners)) != true)
            {
                continue;
            }

            found.Add($"the {role.ToString().ToLowerInvariant()} role ({RolePlacement.RegistrationOf(role)})");
        }

        foreach (var (workType, name) in services.GetService<Singleton.SingletonWorkRegistrations>()?.Works ?? [])
        {
            found.Add($"the singleton work '{name ?? workType.Name}'");
        }

        return found;
    }
}
