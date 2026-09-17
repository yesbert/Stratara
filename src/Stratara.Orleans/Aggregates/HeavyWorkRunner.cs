using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Polly;
using Polly.Retry;
using Stratara.Orleans.Diagnostics;

namespace Stratara.Orleans.Aggregates;

/// <summary>
/// The silo's heavy workers. A pool grain accepts a hand-over, leases it and hands the unit here; the unit runs on
/// one of <see cref="HeavyWorkGrain.MaxLocalWorkers"/> workers, off the activation's scheduler, so a handler that
/// computes without awaiting anything occupies one worker and nothing else — the silo's other units keep running,
/// and the pool keeps accepting and leasing hand-overs while it computes. The permit a unit runs under is taken
/// here, on the worker, inside the unit's own lease.
/// </summary>
/// <remarks>
/// The workers are loops over a channel, started when the host starts, as
/// <see cref="IntentCompletionQueue"/>'s flush loop is: a hosted service has no synchronization context, so a loop
/// runs on the thread pool and the unit it invokes runs there too. Eight workers per silo can hold eight threads of
/// the pool while their handlers compute, which is what heavy work is for; size the silo for it. A unit whose token
/// is cancelled while it waits — the silo is stopping — is run where the cancellation is seen rather than dropped,
/// so it ends its intent's lease and is resumed elsewhere instead of waiting behind the handlers ahead of it. A unit
/// handed in after the host stopped the runner runs on the caller, so nothing is lost to the order in which hosted
/// services stop.
/// </remarks>
internal sealed class HeavyWorkRunner : IHostedService
{
    private readonly Channel<HeavyUnit> _queued = Channel.CreateUnbounded<HeavyUnit>();
    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HeavyWorkRunner> _logger;
    private readonly TimeSpan _renewal;
    private readonly ResiliencePipeline<bool> _acquirePermit;
    private Task[]? _workers;

    public HeavyWorkRunner(
        IServiceProvider services,
        IOptions<HeavyWorkOptions> options,
        TimeProvider timeProvider,
        ILogger<HeavyWorkRunner> logger)
    {
        _services = services;
        _timeProvider = timeProvider;
        _logger = logger;
        _renewal = options.Value.PermitLease / 2;
        _acquirePermit = new ResiliencePipelineBuilder<bool>()
            .AddRetry(new RetryStrategyOptions<bool>
            {
                ShouldHandle = new PredicateBuilder<bool>().HandleResult(false),
                MaxRetryAttempts = int.MaxValue,
                BackoffType = DelayBackoffType.Constant,
                Delay = options.Value.PermitRetry,
            })
            .Build();
    }

    /// <summary>
    /// Runs <paramref name="unit"/> on a worker and completes when it has run or thrown. The unit itself takes the
    /// permit — through <see cref="UnderPermitAsync"/>, inside its lease — so a unit that waits for a worker or a
    /// permit counts as running and a unit that never starts still ends its lease.
    /// </summary>
    /// <param name="unit">What to run; it ends the intent's lease whatever happens to it.</param>
    /// <param name="cancellationToken">Cancelled when the silo stops: a unit still waiting is run at once, which
    /// ends it at the permit it was waiting for.</param>
    public Task RunAsync(Func<Task> unit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var queued = new HeavyUnit(unit);

        // Registered before the unit is queued: a worker that takes it first disposes a registration that is
        // already there, and a cancellation that arrives first runs the unit and leaves the worker nothing to take.
        queued.AbandonOn(cancellationToken);
        if (!_queued.Writer.TryWrite(queued))
        {
            queued.RunAsync().Ignore();
        }

        return queued.Completion.Task;
    }

    /// <summary>
    /// Runs <paramref name="run"/> under a permit, as the unit's own wrapper, so the lease around it ends the intent
    /// whatever the permit does. The release after it cannot change the unit's outcome: a release that fails is
    /// logged, and the permit's lease releases it.
    /// </summary>
    public async Task UnderPermitAsync(Func<Task> run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var silo = _services.GetRequiredService<ILocalSiloDetails>().SiloAddress;
        var permits = _services.GetRequiredService<IGrainFactory>().GetGrain<IHeavyWorkPermitGrain>(0);
        var unitId = Guid.NewGuid();
        await _acquirePermit.ExecuteAsync(async _ => await permits.TryAcquireAsync(unitId, silo), cancellationToken);

        var renewal = new PermitRenewal(permits, unitId, silo, _logger);
        var renewing = _timeProvider.CreateTimer(static state => ((PermitRenewal)state!).Tick(), renewal, _renewal, _renewal);
        try
        {
            await run();
        }
        finally
        {
            await renewing.DisposeAsync();
            await renewal.SettleAsync();
            try
            {
                await permits.ReleaseAsync(unitId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogPermitReleaseFailed(ex, unitId);
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _workers ??= [.. Enumerable.Range(0, HeavyWorkGrain.MaxLocalWorkers).Select(_ => WorkAsync())];
        return Task.CompletedTask;
    }

    /// <summary>Ends the workers once what they hold has run, within the host's shutdown budget.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queued.Writer.TryComplete();
        if (_workers is null)
        {
            return;
        }

        try
        {
            await Task.WhenAll(_workers).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _workers = null;
        }
    }

    private async Task WorkAsync()
    {
        var reader = _queued.Reader;
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var unit))
            {
                await unit.RunAsync();
            }
        }
    }
}

/// <summary>
/// One unit queued for a worker. It runs once: either a worker takes it, or the cancellation of the token it was
/// queued with takes it and runs it where that is seen, which ends it at the permit it waits for.
/// </summary>
internal sealed class HeavyUnit(Func<Task> run)
{
    private CancellationTokenRegistration _abandon;
    private int _taken;

    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Runs the unit at once when <paramref name="cancellationToken"/> is cancelled and no worker has taken it.</summary>
    public void AbandonOn(CancellationToken cancellationToken) =>
        _abandon = cancellationToken.Register(static state => ((HeavyUnit)state!).RunAsync().Ignore(), this);

    /// <summary>Runs the unit unless it is already taken, and completes what the pool grain awaits.</summary>
    public async Task RunAsync()
    {
        if (Interlocked.CompareExchange(ref _taken, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await run();
            Completion.TrySetResult();
        }
        catch (Exception ex)
        {
            Completion.TrySetException(ex);
        }
        finally
        {
            _abandon.Dispose();
        }
    }
}
