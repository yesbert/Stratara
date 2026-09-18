using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.GrainDirectory;
using Orleans.Runtime;
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
    /// the cluster declares the holder dead. The grain keeping the permits admits no new unit for this period after it
    /// is activated, so the units still running under a lost keeper are counted again before new ones start.
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

    /// <summary>
    /// Takes back a running unit whose permit was not held when renewed; <see langword="false"/> when it is refused, and
    /// the unit runs outside the bound until a later call takes it.
    /// </summary>
    [Alias("ReclaimAsync")]
    Task<bool> ReclaimAsync(Guid unitId, SiloAddress holder);

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
/// later writer where both append — and here rather than anywhere, so the number running is bounded: per silo by
/// the <see cref="HeavyWorkRunner"/>'s workers, and across the cluster by the permit grain — the limit the silos
/// alone cannot give. There are as many pools as the cluster-wide limit needs, placed on silos of the command role,
/// where the handlers are. A hand-over is accepted at once, and the intent's hand-over is renewed from that moment —
/// while it waits for a worker, while it waits for a permit and while it runs — so a burst that queues units for
/// longer than the grace hands none of them over twice; it is completed after the handler, so a crash in between is
/// resumed like any other intent. The unit itself runs on one of the silo's workers, off this activation's
/// scheduler, so a handler that computes without awaiting anything neither stops the silo's other units nor keeps
/// the next hand-over from being accepted and leased. A unit renews its permit while it runs, so a permit outlives
/// its unit only by a lease; both renewals run from timers of their own. A hand-over the activation already holds is
/// not accepted twice.
/// </summary>
[CommandsRolePlacementFilter]
internal sealed class HeavyWorkGrain(
    IServiceScopeFactory scopeFactory,
    HeavyWorkRunner runner,
    SiloStopSignal stopSignal) : Grain, IHeavyWorkGrain
{
    /// <summary>How many units one silo runs at once — the workers of its <see cref="HeavyWorkRunner"/>.</summary>
    public const int MaxLocalWorkers = 8;

    /// <summary>How many pools the cluster-wide limit needs; at least one.</summary>
    public static int PoolsFor(int clusterWideLimit) => Math.Max(1, (clusterWideLimit + MaxLocalWorkers - 1) / MaxLocalWorkers);

    private readonly HashSet<Guid> _held = [];
    private readonly CancellationTokenSource _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopSignal.Stopping);
    private readonly HashSet<Task> _inFlight = [];

    /// <summary>
    /// Accepts the hand-over: the intent's lease starts here, before the unit is queued for a worker, so a unit
    /// waiting for a worker or a permit counts as running. The unit itself — the handler, inside its lease, under a
    /// permit — runs on one of the silo's workers.
    /// </summary>
    public async Task ExecuteIntentAsync(Guid intentId, AggregateCommandEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!_held.Add(intentId))
        {
            return;
        }

        var scope = scopeFactory.CreateScope();
        Task run;
        try
        {
            var lease = await IntentLease.StartAsync(scope.ServiceProvider, intentId);
            run = runner.RunAsync(
                () => CommandExecution.RunIntentAsync(
                    scope.ServiceProvider,
                    envelope,
                    intentId,
                    lease,
                    around: unit => runner.UnderPermitAsync(unit, _stopping.Token),
                    aggregateId: null,
                    _stopping.Token),
                _stopping.Token);
        }
        catch
        {
            _held.Remove(intentId);
            scope.Dispose();
            throw;
        }

        _inFlight.Add(run);
        try
        {
            await run;
        }
        finally
        {
            _inFlight.Remove(run);
            _held.Remove(intentId);
            scope.Dispose();
        }
    }

    /// <summary>
    /// Waits for the units in flight within the deactivation budget; past it, cancels their token — a unit still
    /// queued for a worker ends at the permit it was waiting for, a unit waiting for a permit stops waiting, and a
    /// running handler is told to stop — and waits for them to end.
    /// </summary>
    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        await AggregateGrain.StopRunningAsync(_stopping, _inFlight.Count == 0 ? null : Task.WhenAll(_inFlight), cancellationToken);
        _stopping.Dispose();
        await base.OnDeactivateAsync(reason, cancellationToken);
    }
}

/// <summary>
/// Renews a running unit's permit at half its lease, from the timer's thread. A permit that is no longer held — its lease
/// lapsed on a renewal that failed, or the permit grain was activated again without it — is reclaimed, so the permit
/// grain counts the running unit against the bound once more. A refused reclaim is logged once per loss and leaves the
/// unit running outside the bound; every following tick reclaims again instead of renewing until one is taken. One
/// renewal at a time; a tick that finds one running does nothing.
/// </summary>
internal sealed class PermitRenewal(IHeavyWorkPermitGrain permits, Guid unitId, SiloAddress holder, ILogger logger)
{
    private volatile Task _inFlight = Task.CompletedTask;
    private volatile bool _held = true;
    private int _renewing;

    /// <summary>Whether the unit counted against the bound at the last answer of the permit grain.</summary>
    public bool Held => _held;

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
            if (_held)
            {
                if (await permits.RenewAsync(unitId))
                {
                    return;
                }

                logger.LogPermitRenewalLost(unitId);
            }

            if (await permits.ReclaimAsync(unitId, holder))
            {
                _held = true;
                return;
            }

            if (_held)
            {
                _held = false;
                logger.LogPermitReclaimRefused(unitId, holder.ToString());
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

/// <summary>
/// The cluster-wide bound behind the permits: single activation, one call at a time, over a <see cref="PermitLedger"/>.
/// Released permits are reconciled on every acquisition and on a timer of the grain's own, so a crashed worker does not
/// shrink the bound for longer than a lease; a new activation admits no new unit for one lease, so the units a lost
/// activation had admitted are counted again before the bound applies to new ones.
/// </summary>
[GrainDirectory(GrainDirectories.Durable)]
internal sealed class HeavyWorkPermitGrain(
    IOptions<HeavyWorkOptions> options,
    IClusterMembershipService membership,
    TimeProvider timeProvider,
    ILogger<HeavyWorkPermitGrain> logger) : Grain, IHeavyWorkPermitGrain
{
    private readonly PermitLedger _ledger = new(options.Value.ClusterWideLimit, options.Value.PermitLease, timeProvider);
    private IGrainTimer? _reconcile;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _reconcile ??= this.RegisterGrainTimer(
            _ =>
            {
                Reconcile();
                return Task.CompletedTask;
            },
            new GrainTimerCreationOptions { DueTime = options.Value.PermitLease, Period = options.Value.PermitLease, Interleave = false, KeepAlive = true });
        return base.OnActivateAsync(cancellationToken);
    }

    public Task<bool> TryAcquireAsync(Guid unitId, SiloAddress holder)
    {
        Reconcile();
        return Task.FromResult(_ledger.TryAcquire(unitId, holder));
    }

    public Task<bool> ReclaimAsync(Guid unitId, SiloAddress holder)
    {
        Reconcile();
        return Task.FromResult(_ledger.Reclaim(unitId, holder));
    }

    public Task<bool> RenewAsync(Guid unitId) => Task.FromResult(_ledger.Renew(unitId));

    public Task ReleaseAsync(Guid unitId)
    {
        _ledger.Release(unitId);
        return Task.CompletedTask;
    }

    public Task<int> InUseAsync()
    {
        Reconcile();
        return Task.FromResult(_ledger.InUse);
    }

    private void Reconcile()
    {
        foreach (var released in _ledger.Reconcile(membership.CurrentSnapshot))
        {
            logger.LogPermitReleasedByExpiry(released.UnitId, released.Holder.ToString(), released.HolderDead ? "holder declared dead" : "lease lapsed");
        }
    }
}
