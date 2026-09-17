using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.GrainDirectory;
using Stratara.Abstractions.Singleton;
using Stratara.Orleans.Diagnostics;

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
/// grain directory's single-activation guarantee is what makes this "once per cluster". A run that throws is logged
/// and the timer's next tick runs the work again.
/// </summary>
[GrainDirectory(GrainDirectories.Durable)]
[SingletonWorkPlacementFilter]
internal sealed class SingletonWorkGrain(
    IServiceScopeFactory scopeFactory,
    IOptions<SingletonWorkOptions> options,
    ILogger<SingletonWorkGrain> logger) : Grain, ISingletonWorkGrain, IRemindable
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
        try
        {
            await ResolveWork(scope.ServiceProvider).RunAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A cancellation the work was not asked for — an HTTP client's timeout — is a failure like any other; only
            // the stop this grain asked for passes through unlogged.
            logger.LogSingletonWorkFailed(exception, this.GetPrimaryKeyString());
            return;
        }

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
/// Asks every registered work's grain to run once the silo is active — a stage of the silo's own
/// lifecycle, so it runs when the silo can take a call, whatever order the host registered the silo and
/// the framework's composites in. Idempotent across silos: a grain that already runs elsewhere just
/// re-arms its reminder. This is where a work registered with its name is first constructed, and where its name is
/// compared with the registered one.
/// </summary>
internal sealed class SingletonWorkStarter(IServiceScopeFactory scopeFactory, IGrainFactory grainFactory) : ILifecycleParticipant<ISiloLifecycle>
{
    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(nameof(SingletonWorkStarter), ServiceLifecycleStage.Active, StartAsync);

    /// <exception cref="InvalidOperationException">
    /// A work's name is not the name it was registered under, or two works carry one name.
    /// </exception>
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var works = scope.ServiceProvider.GetServices<ISingletonWork>().ToList();
        var registrations = scope.ServiceProvider.GetService<SingletonWorkRegistrations>();
        foreach (var work in works)
        {
            registrations?.EnsureNamed(work);
        }

        EnsureOneWorkPerName(works);

        foreach (var work in works)
        {
            await grainFactory.GetGrain<ISingletonWorkGrain>(work.Name).EnsureRunningAsync();
        }
    }

    /// <summary>
    /// A name is one work's: it names the grain that runs it, so a second work of the same name would never run and
    /// nothing would say so.
    /// </summary>
    /// <param name="works">The works the silo registered.</param>
    /// <exception cref="InvalidOperationException">Two works carry one name; the message names them and it.</exception>
    internal static void EnsureOneWorkPerName(IReadOnlyList<ISingletonWork> works)
    {
        var shared = works.GroupBy(work => work.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (shared is not null)
        {
            throw new InvalidOperationException(
                $"The singleton works {string.Join(" and ", shared.Select(work => work.GetType().Name))} are all named '{shared.Key}'. One name is one work — it names the grain that runs it — so only the first would ever run; give each its own Name.");
        }
    }
}
