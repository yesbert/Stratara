using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Diagnostics;
using Stratara.Orleans.Hosting;
using Stratara.Orleans.Sagas;
using Stratara.Shared.Partitioning;

namespace Stratara.Orleans.Projections;

/// <summary>Wakes the projection readers of a silo of the projection role; the key only spreads the calls.</summary>
[Alias("Stratara.Orleans.IProjectionWakeUpGrain")]
internal interface IProjectionWakeUpGrain : IGrainWithIntegerKey
{
    /// <summary>Wakes every projection's reader of the partitions <paramref name="buckets"/> fall into on this silo.</summary>
    [AlwaysInterleave]
    [Alias("WakeAsync")]
    Task WakeAsync(int[] buckets);
}

/// <summary>Wakes the saga readers of a silo of the saga role; the key only spreads the calls.</summary>
[Alias("Stratara.Orleans.ISagaWakeUpGrain")]
internal interface ISagaWakeUpGrain : IGrainWithIntegerKey
{
    /// <summary>Wakes every saga's reader of the partitions <paramref name="buckets"/> fall into on this silo.</summary>
    [AlwaysInterleave]
    [Alias("WakeAsync")]
    Task WakeAsync(int[] buckets);
}

/// <summary>
/// The silo's side of a wake-up from a host that hosts no reader of the role: the host names the buckets its commit
/// touched, and the grain folds them into this silo's partitions and nudges the readers the silo registered for the
/// role. The host needs to know neither the readers' names nor the silo's partition count. A nudge that cannot be sent
/// is lost like any other and logged at debug; the readers' poll reads the commit.
/// </summary>
internal abstract class StoreWakeUpGrain<TTarget>(IServiceScopeFactory scopeFactory, IOptions<CommitOrderOptions> commitOrder, ILogger logger) : Grain
    where TTarget : INudgeTarget
{
    private readonly int _partitionCount = commitOrder.Value.PartitionCount;

    public async Task WakeAsync(int[] buckets)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await WakeLocalAsync<TTarget>(scope.ServiceProvider, GrainFactory, buckets, _partitionCount, logger);
    }

    /// <summary>
    /// Nudges, for every partition <paramref name="buckets"/> fall into under <paramref name="partitionCount"/>, the
    /// readers of every <typeparamref name="TRole"/> target <paramref name="services"/> registers. A nudge that cannot be
    /// sent is logged at debug and does not keep the others from being sent.
    /// </summary>
    public static async Task WakeLocalAsync<TRole>(IServiceProvider services, IGrainFactory grainFactory, int[] buckets, int partitionCount, ILogger logger)
        where TRole : INudgeTarget
    {
        ArgumentNullException.ThrowIfNull(buckets);
        var partitions = buckets.Select(bucket => PartitionMap.PartitionOf(bucket, partitionCount)).Distinct().ToList();
        foreach (var target in services.GetServices<INudgeTarget>().OfType<TRole>())
        {
            foreach (var partition in partitions)
            {
                try
                {
                    await target.NudgeAsync(grainFactory, partition);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogNudgeFailed(exception, string.Join(", ", target.ConsumerNames), partition);
                    }
                }
            }
        }
    }
}

[ProjectionsRolePlacementFilter]
internal sealed class ProjectionWakeUpGrain(IServiceScopeFactory scopeFactory, IOptions<CommitOrderOptions> commitOrder, ILogger<ProjectionWakeUpGrain> logger)
    : StoreWakeUpGrain<ProjectionNudgeTarget>(scopeFactory, commitOrder, logger), IProjectionWakeUpGrain;

[SagasRolePlacementFilter]
internal sealed class SagaWakeUpGrain(IServiceScopeFactory scopeFactory, IOptions<CommitOrderOptions> commitOrder, ILogger<SagaWakeUpGrain> logger)
    : StoreWakeUpGrain<SagaNudgeTarget>(scopeFactory, commitOrder, logger), ISagaWakeUpGrain;

/// <summary>
/// The host's side: after a commit, wakes the readers of every store-reading role this host does not host itself,
/// through the wake-up grain of the role, one call per key the commit's buckets spread over. Nothing waits for a call;
/// each is observed. A role whose grain cannot be placed because no silo of the cluster hosts it is set aside for
/// <see cref="StoreWakeUpOptions.AbsentRoleRetry"/> and then tried again; any other failure is a lost wake-up.
/// </summary>
internal sealed class ClusterWakeUps
{
    private readonly IReadOnlyList<RoleWakeUp> _roles;
    private readonly int _keys;

    public ClusterWakeUps(IServiceProvider services, IOptions<StoreWakeUpOptions> options, IOptions<CommitOrderOptions> commitOrder, TimeProvider timeProvider, ILogger<ClusterWakeUps> logger)
    {
        var local = services.GetService<RolePlacement.PublishedRoles>()?.Roles ?? [];
        var retry = options.Value.AbsentRoleRetry;
        _keys = commitOrder.Value.PartitionCount;
        _roles =
        [
            .. new[]
            {
                new RoleWakeUp(ExecutionRole.Projections, static (grains, key, buckets) => grains.GetGrain<IProjectionWakeUpGrain>(key).WakeAsync(buckets), retry, timeProvider, logger),
                new RoleWakeUp(ExecutionRole.Sagas, static (grains, key, buckets) => grains.GetGrain<ISagaWakeUpGrain>(key).WakeAsync(buckets), retry, timeProvider, logger),
            }.Where(role => !local.Contains(role.Role)),
        ];
    }

    /// <summary>The roles this host wakes through the cluster: those it does not host itself.</summary>
    public IReadOnlyList<ExecutionRole> Roles => [.. _roles.Select(role => role.Role)];

    /// <summary>Wakes the readers of the buckets the commit's streams fall into; returns at once.</summary>
    public void Wake(IGrainFactory grainFactory, IEnumerable<Guid> streamIds)
    {
        var byKey = streamIds
            .Select(BucketCalculator.GetBucketId)
            .Distinct()
            .GroupBy(bucket => PartitionMap.PartitionOf(bucket, _keys))
            .Select(group => (Key: group.Key, Buckets: group.ToArray()))
            .ToList();
        foreach (var role in _roles)
        {
            role.Wake(grainFactory, byKey);
        }
    }

    /// <summary>One role's wake-ups and whether they are set aside because no silo hosts the role.</summary>
    private sealed class RoleWakeUp(
        ExecutionRole role,
        Func<IGrainFactory, long, int[], Task> call,
        TimeSpan retry,
        TimeProvider timeProvider,
        ILogger logger)
    {
        private readonly object _gate = new();
        private DateTimeOffset _setAsideUntil = DateTimeOffset.MinValue;
        private bool _absent;

        public ExecutionRole Role => role;

        private string Name => role.ToString().ToLowerInvariant();

        public void Wake(IGrainFactory grainFactory, IReadOnlyList<(int Key, int[] Buckets)> byKey)
        {
            lock (_gate)
            {
                if (timeProvider.GetUtcNow() < _setAsideUntil)
                {
                    return;
                }
            }

            foreach (var (key, buckets) in byKey)
            {
                Task sent;
                try
                {
                    sent = call(grainFactory, key, buckets);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Failed(exception, key);
                    continue;
                }

                sent.ContinueWith(
                    static (completed, state) =>
                    {
                        var (wakeUp, partition) = ((RoleWakeUp, int))state!;
                        if (completed.IsFaulted)
                        {
                            wakeUp.Failed(completed.Exception!.GetBaseException(), partition);
                        }
                        else if (completed.IsCompletedSuccessfully)
                        {
                            wakeUp.Reached();
                        }
                    },
                    (this, key),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default).Ignore();
            }
        }

        private void Failed(Exception exception, int key)
        {
            if (!RolePlacement.NamesNoSiloFor(exception, role))
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogNudgeFailed(exception, $"the {Name} role", key);
                }

                return;
            }

            bool found;
            lock (_gate)
            {
                _setAsideUntil = timeProvider.GetUtcNow() + retry;
                found = !_absent;
                _absent = true;
            }

            if (found && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogStoreWakeUpRoleAbsent(Name, retry);
            }
        }

        private void Reached()
        {
            bool back;
            lock (_gate)
            {
                back = _absent;
                _absent = false;
            }

            if (back && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogStoreWakeUpRoleBack(Name);
            }
        }
    }
}
