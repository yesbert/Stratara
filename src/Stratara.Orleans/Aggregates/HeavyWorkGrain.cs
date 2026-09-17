using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.GrainDirectory;
using Orleans.Runtime;
using Polly;
using Polly.Retry;
using Stratara.Diagnostics;
using Stratara.Orleans.Diagnostics;
using Stratara.Orleans.Hosting;

namespace Stratara.Orleans.Aggregates;

/// <summary>Settings for heavy work.</summary>
/// <remarks>
/// A heavy command runs outside its aggregate's turn and order: a command naming the same aggregate does not wait
/// for it, and where both append, the store's version check refuses the later one, which is resumed like any
/// failing command. Mark a command heavy only where it rarely meets a stream of other commands on its aggregate.
/// </remarks>
public sealed class HeavyWorkOptions
{
    /// <summary>How many heavy units may run at once across the whole cluster.</summary>
    public int ClusterWideLimit { get; set; } = 8;

    /// <summary>How long a worker waits before asking for a permit again.</summary>
    public TimeSpan PermitRetry { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long a permit is held without renewal before it is released. A running unit renews its permit
    /// at half this period; a permit whose holder died is released after it at the latest, or as soon as
    /// the cluster declares the holder dead.
    /// </summary>
    public TimeSpan PermitLease { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>A bounded pool of workers; the key names the pool, and the pools are placed on silos of the command role.</summary>
[Alias("Stratara.Orleans.IHeavyWorkGrain")]
internal interface IHeavyWorkGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// Accepts a heavy hand-over — its lease starts before it waits for a worker or a permit — and completes once the
    /// unit has run. The call interleaves with the units already running, so no hand-over waits unleased in a queue.
    /// </summary>
    [AlwaysInterleave]
    [Alias("ExecuteIntentAsync")]
    Task ExecuteIntentAsync(Guid intentId, AggregateCommandEnvelope envelope);
}

/// <summary>One grain for the cluster that hands out the permits heavy work runs under.</summary>
[Alias("Stratara.Orleans.IHeavyWorkPermitGrain")]
internal interface IHeavyWorkPermitGrain : IGrainWithIntegerKey
{
    /// <summary>Takes a permit for a unit held by <paramref name="holder"/>, unless the bound is reached.</summary>
    [Alias("TryAcquireAsync")]
    Task<bool> TryAcquireAsync(Guid unitId, SiloAddress holder);

    /// <summary>Extends a unit's lease; <see langword="false"/> when the permit was already released.</summary>
    [Alias("RenewAsync")]
    Task<bool> RenewAsync(Guid unitId);

    [Alias("ReleaseAsync")]
    Task ReleaseAsync(Guid unitId);

    [Alias("InUseAsync")]
    Task<int> InUseAsync();
}

/// <summary>
/// Heavy work runs here rather than in the aggregate's grain, so a long unit does not hold an
/// aggregate's turn — it runs beside the aggregate's other commands, and the store's version check refuses the
/// later writer where both append — and here rather than anywhere, so the number running is bounded: per pool by
/// its worker slots, and across the cluster by the permit grain — the limit the pools alone cannot give. There are
/// as many pools as the cluster-wide limit needs, placed on silos of the command role, where the handlers are. A
/// hand-over is accepted at once, and the intent's hand-over is renewed from that moment — while it waits for a
/// slot, while it waits for a permit and while it runs — so a burst that queues units for longer than the grace
/// hands none of them over twice; it is completed after the handler, so a crash in between is resumed like any other
/// intent. A unit renews its permit while it runs, so a permit outlives its unit only by a lease. Both renewals run
/// from timers of their own, off the activation's scheduler, so a handler that computes without yielding is renewed
/// all the same. A hand-over the activation already holds is not accepted twice.
/// </summary>
[CommandsRolePlacementFilter]
internal sealed class HeavyWorkGrain(
    IServiceScopeFactory scopeFactory,
    IOptions<HeavyWorkOptions> options,
    ILocalSiloDetails localSilo,
    TimeProvider timeProvider,
    ILogger<HeavyWorkGrain> logger) : Grain, IHeavyWorkGrain
{
    public const int MaxLocalWorkers = 8;

    /// <summary>How many pools the cluster-wide limit needs, each with <see cref="MaxLocalWorkers"/> slots; at least one.</summary>
    public static int PoolsFor(int clusterWideLimit) => Math.Max(1, (clusterWideLimit + MaxLocalWorkers - 1) / MaxLocalWorkers);

    private readonly TimeSpan _renewal = options.Value.PermitLease / 2;
    private readonly SemaphoreSlim _slots = new(MaxLocalWorkers, MaxLocalWorkers);
    private readonly HashSet<Guid> _held = [];

    private readonly ResiliencePipeline<bool> _acquirePermit = new ResiliencePipelineBuilder<bool>()
        .AddRetry(new RetryStrategyOptions<bool>
        {
            ShouldHandle = new PredicateBuilder<bool>().HandleResult(false),
            MaxRetryAttempts = int.MaxValue,
            BackoffType = DelayBackoffType.Constant,
            Delay = options.Value.PermitRetry,
        })
        .Build();

    public async Task ExecuteIntentAsync(Guid intentId, AggregateCommandEnvelope envelope)
    {
        if (!_held.Add(intentId))
        {
            return;
        }

        try
        {
            await CommandExecution.RunAsync(scopeFactory, envelope, intentId, UnderSlotAndPermitAsync);
        }
        finally
        {
            _held.Remove(intentId);
        }
    }

    /// <summary>Runs the unit in one of the activation's slots, under a permit; the intent's lease is already renewing.</summary>
    private async Task UnderSlotAndPermitAsync(Func<Task> run)
    {
        await _slots.WaitAsync();
        try
        {
            await UnderPermitAsync(run);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// Runs the unit under a permit. The release after it cannot change the unit's outcome: a release that fails is
    /// logged, and the permit's lease releases it.
    /// </summary>
    private async Task UnderPermitAsync(Func<Task> run)
    {
        var permits = GrainFactory.GetGrain<IHeavyWorkPermitGrain>(0);
        var unitId = Guid.NewGuid();
        await _acquirePermit.ExecuteAsync(async _ => await permits.TryAcquireAsync(unitId, localSilo.SiloAddress));

        var renewal = new PermitRenewal(permits, unitId, localSilo.SiloAddress, logger);
        var renewing = timeProvider.CreateTimer(static state => ((PermitRenewal)state!).Tick(), renewal, _renewal, _renewal);
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
                logger.LogPermitReleaseFailed(ex, unitId);
            }
        }
    }

    /// <summary>
    /// Renews the unit's permit at half its lease, from the timer's thread. A permit that is no longer held — its
    /// lease lapsed on a renewal that failed, or the permit grain was activated again without it — is taken again, so
    /// the permit grain counts the running unit against the bound once more. One renewal at a time; a tick that
    /// finds one running does nothing.
    /// </summary>
    private sealed class PermitRenewal(IHeavyWorkPermitGrain permits, Guid unitId, SiloAddress holder, ILogger logger)
    {
        private volatile Task _inFlight = Task.CompletedTask;
        private int _renewing;

        public void Tick()
        {
            if (Interlocked.CompareExchange(ref _renewing, 1, 0) != 0)
            {
                return;
            }

            _inFlight = RenewOnceAsync();
        }

        /// <summary>Waits for the renewal in flight, once the timer is disposed and no tick can start another.</summary>
        public Task SettleAsync() => _inFlight;

        private async Task RenewOnceAsync()
        {
            try
            {
                if (!await permits.RenewAsync(unitId))
                {
                    logger.LogPermitRenewalLost(unitId);
                    await permits.TryAcquireAsync(unitId, holder);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex;
            }
            finally
            {
                Volatile.Write(ref _renewing, 0);
            }
        }
    }
}

/// <summary>
/// The cluster-wide bound behind the permits: single activation, one call at a time. Each permit
/// records the silo holding it and when its lease runs out. A permit is released when its unit
/// releases it, when its lease lapses without renewal, or when the cluster declares its holder dead —
/// checked on every acquisition and on a timer of the grain's own — so a crashed worker does not shrink
/// the bound for longer than a lease.
/// </summary>
[GrainDirectory(GrainDirectories.Durable)]
internal sealed class HeavyWorkPermitGrain(
    IOptions<HeavyWorkOptions> options,
    IClusterMembershipService membership,
    TimeProvider timeProvider,
    ILogger<HeavyWorkPermitGrain> logger) : Grain, IHeavyWorkPermitGrain
{
    private readonly int _limit = options.Value.ClusterWideLimit;
    private readonly TimeSpan _lease = options.Value.PermitLease;
    private readonly Dictionary<Guid, Permit> _permits = new();
    private IGrainTimer? _reconcile;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _reconcile ??= this.RegisterGrainTimer(
            _ =>
            {
                Reconcile();
                return Task.CompletedTask;
            },
            new GrainTimerCreationOptions { DueTime = _lease, Period = _lease, Interleave = false, KeepAlive = true });
        return base.OnActivateAsync(cancellationToken);
    }

    public Task<bool> TryAcquireAsync(Guid unitId, SiloAddress holder)
    {
        Reconcile();
        if (_permits.ContainsKey(unitId))
        {
            return Task.FromResult(true);
        }

        if (_permits.Count >= _limit)
        {
            return Task.FromResult(false);
        }

        _permits[unitId] = new Permit(holder, timeProvider.GetUtcNow() + _lease);
        ApplicationDiagnostics.Metrics.OrleansHeavyPermitsInUse.Add(1);
        return Task.FromResult(true);
    }

    public Task<bool> RenewAsync(Guid unitId)
    {
        if (!_permits.TryGetValue(unitId, out var permit))
        {
            return Task.FromResult(false);
        }

        _permits[unitId] = permit with { ExpiresAt = timeProvider.GetUtcNow() + _lease };
        return Task.FromResult(true);
    }

    public Task ReleaseAsync(Guid unitId)
    {
        if (_permits.Remove(unitId))
        {
            ApplicationDiagnostics.Metrics.OrleansHeavyPermitsInUse.Add(-1);
        }

        return Task.CompletedTask;
    }

    public Task<int> InUseAsync()
    {
        Reconcile();
        return Task.FromResult(_permits.Count);
    }

    /// <summary>Releases every permit whose lease lapsed or whose holder the cluster has declared dead.</summary>
    private void Reconcile()
    {
        var now = timeProvider.GetUtcNow();
        var snapshot = membership.CurrentSnapshot;
        foreach (var (unitId, permit) in _permits.ToList())
        {
            var holderDead = snapshot.GetSiloStatus(permit.Holder) == SiloStatus.Dead;
            if (!holderDead && permit.ExpiresAt > now)
            {
                continue;
            }

            _permits.Remove(unitId);
            ApplicationDiagnostics.Metrics.OrleansHeavyPermitsInUse.Add(-1);
            logger.LogPermitReleasedByExpiry(unitId, permit.Holder.ToString(), holderDead ? "holder declared dead" : "lease lapsed");
        }
    }

    private sealed record Permit(SiloAddress Holder, DateTimeOffset ExpiresAt);
}
