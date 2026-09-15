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
/// registration, rather than at the first activation that needs it.
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

    /// <exception cref="InvalidOperationException">No grain directory is registered under <see cref="GrainDirectories.Durable"/>.</exception>
    private Task CheckAsync(CancellationToken cancellationToken)
    {
        if (services.GetKeyedService<IGrainDirectory>(GrainDirectories.Durable) is not null)
        {
            return Task.CompletedTask;
        }

        logger.LogDirectoryCheckFailed(GrainDirectories.Durable);
        throw new InvalidOperationException(
            $"The Orleans execution model places its single-activation grains in the grain directory '{GrainDirectories.Durable}', and none is registered under that name. Register one with AddStrataraOrleans on the silo builder, for example silo.AddStrataraOrleans((s, name) => s.AddRedisGrainDirectory(name, options => ...)).");
    }
}
