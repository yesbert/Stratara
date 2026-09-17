using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Stratara.Orleans.Hosting;

/// <summary>
/// The moment a stopping silo tells the handlers on its grain paths to stop. When the silo's graceful shutdown begins,
/// the runtime waits for the requests its activations are running before it deactivates them; a handler that does not
/// return keeps that wait — and the handler's own work — going in a process that is on its way out. The signal is
/// requested once the deactivation budget, <see cref="GrainCollectionOptions.DeactivationTimeout"/>, has passed since
/// the shutdown began, so a handler that completes within the budget is never cancelled.
/// </summary>
internal sealed class SiloStopSignal(IOptions<GrainCollectionOptions> grainCollection) : ILifecycleParticipant<ISiloLifecycle>, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Requested a deactivation budget after the silo began to stop.</summary>
    public CancellationToken Stopping => _stopping.Token;

    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(nameof(SiloStopSignal), ServiceLifecycleStage.Active, static _ => Task.CompletedTask, OnStopAsync);

    public void Dispose() => _stopping.Dispose();

    /// <summary>Registers the signal once, whichever role registration or silo configuration asks for it first.</summary>
    public static void Register(IServiceCollection services)
    {
        services.TryAddSingleton<SiloStopSignal>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILifecycleParticipant<ISiloLifecycle>, SiloStopSignal>(sp => sp.GetRequiredService<SiloStopSignal>()));
    }

    private Task OnStopAsync(CancellationToken cancellationToken)
    {
        _stopping.CancelAfter(grainCollection.Value.DeactivationTimeout);
        return Task.CompletedTask;
    }
}
