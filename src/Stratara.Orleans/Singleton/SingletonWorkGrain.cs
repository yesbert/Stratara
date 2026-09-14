using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.GrainDirectory;

namespace Stratara.Orleans.Singleton;

/// <summary>One grain per registered <see cref="ISingletonWork"/>, keyed by its name.</summary>
[Alias("Stratara.Orleans.ISingletonWorkGrain")]
internal interface ISingletonWorkGrain : IGrainWithStringKey
{
    [Alias("EnsureRunningAsync")]
    Task EnsureRunningAsync();

    [Alias("RunsAsync")]
    Task<long> RunsAsync();
}

/// <summary>
/// Runs its work on a grain timer, which the runtime never lets overlap with itself or with a
/// call, and keeps a reminder so that a silo's loss brings the grain back somewhere else. The
/// grain directory's single-activation guarantee is what makes this "once per cluster".
/// </summary>
[GrainDirectory(GrainDirectories.Durable)]
internal sealed class SingletonWorkGrain(
    IServiceScopeFactory scopeFactory,
    IOptions<SingletonWorkOptions> options) : Grain, ISingletonWorkGrain, IRemindable
{
    private const string KeepAliveReminder = "keep-alive";

    private readonly TimeSpan _keepAlivePeriod = options.Value.KeepAlivePeriod;
    private IGrainTimer? _timer;
    private long _runs;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        StartTimer();
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task EnsureRunningAsync()
    {
        StartTimer();
        await this.RegisterOrUpdateReminder(KeepAliveReminder, _keepAlivePeriod, _keepAlivePeriod);
    }

    public Task<long> RunsAsync() => Task.FromResult(_runs);

    Task IRemindable.ReceiveReminder(string reminderName, TickStatus status)
    {
        StartTimer();
        return Task.CompletedTask;
    }

    private void StartTimer()
    {
        if (_timer is not null)
        {
            return;
        }

        var period = ResolvePeriod();
        _timer = this.RegisterGrainTimer(RunOnceAsync, new GrainTimerCreationOptions
        {
            DueTime = period,
            Period = period,
            Interleave = false,
            KeepAlive = true,
        });
    }

    private TimeSpan ResolvePeriod()
    {
        using var scope = scopeFactory.CreateScope();
        return ResolveWork(scope.ServiceProvider).Period;
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        await ResolveWork(scope.ServiceProvider).RunAsync(cancellationToken);
        _runs++;
    }

    private ISingletonWork ResolveWork(IServiceProvider services)
    {
        var name = this.GetPrimaryKeyString();
        return services.GetServices<ISingletonWork>().FirstOrDefault(work => work.Name == name)
               ?? throw new InvalidOperationException($"No ISingletonWork named '{name}' is registered on this silo.");
    }
}

/// <summary>
/// Asks every registered work's grain to run when the silo starts. Idempotent across silos: a grain
/// that already runs elsewhere just re-arms its reminder.
/// </summary>
internal sealed class SingletonWorkStarter(IServiceScopeFactory scopeFactory, IGrainFactory grainFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        foreach (var work in scope.ServiceProvider.GetServices<ISingletonWork>())
        {
            await grainFactory.GetGrain<ISingletonWorkGrain>(work.Name).EnsureRunningAsync();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
