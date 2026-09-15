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

namespace Stratara.Orleans.Aggregates;

/// <summary>Settings for heavy work.</summary>
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

/// <summary>A bounded pool of workers per silo; the key is ignored.</summary>
[Alias("Stratara.Orleans.IHeavyWorkGrain")]
internal interface IHeavyWorkGrain : IGrainWithIntegerKey
{
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
/// aggregate's turn, and here rather than anywhere, so the number running is bounded: per silo by
/// the worker pool, and across the cluster by the permit grain — the limit a stateless worker's
/// pool alone cannot give. The intent's hand-over is renewed from the moment it arrives, while it
/// waits for a permit and while it runs, and it is completed after the handler, so a crash in between
/// is resumed like any other intent. A unit renews its permit while it runs, so a permit outlives its
/// unit only by a lease.
/// </summary>
[StatelessWorker(HeavyWorkGrain.MaxLocalWorkers)]
internal sealed class HeavyWorkGrain(
    IServiceScopeFactory scopeFactory,
    IOptions<HeavyWorkOptions> options,
    ILocalSiloDetails localSilo,
    TimeProvider timeProvider) : Grain, IHeavyWorkGrain
{
    public const int MaxLocalWorkers = 8;

    private readonly TimeSpan _renewal = options.Value.PermitLease / 2;

    private readonly ResiliencePipeline<bool> _acquirePermit = new ResiliencePipelineBuilder<bool>()
        .AddRetry(new RetryStrategyOptions<bool>
        {
            ShouldHandle = new PredicateBuilder<bool>().HandleResult(false),
            MaxRetryAttempts = int.MaxValue,
            BackoffType = DelayBackoffType.Constant,
            Delay = options.Value.PermitRetry,
        })
        .Build();

    public Task ExecuteIntentAsync(Guid intentId, AggregateCommandEnvelope envelope) =>
        CommandExecution.RunAsync(scopeFactory, envelope, intentId, UnderPermitAsync);

    private async Task UnderPermitAsync(Func<Task> run)
    {
        var permits = GrainFactory.GetGrain<IHeavyWorkPermitGrain>(0);
        var unitId = Guid.NewGuid();
        await _acquirePermit.ExecuteAsync(async _ => await permits.TryAcquireAsync(unitId, localSilo.SiloAddress));

        using var stop = new CancellationTokenSource();
        var renewing = RenewWhileRunningAsync(permits, unitId, stop.Token);
        try
        {
            await run();
        }
        finally
        {
            await stop.CancelAsync();
            await renewing;
            await permits.ReleaseAsync(unitId);
        }
    }

    private async Task RenewWhileRunningAsync(IHeavyWorkPermitGrain permits, Guid unitId, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_renewal, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await permits.RenewAsync(unitId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _ = ex;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
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
