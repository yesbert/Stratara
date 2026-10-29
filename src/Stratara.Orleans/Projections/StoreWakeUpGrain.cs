using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Diagnostics;
using Stratara.Orleans.Hosting;
using Stratara.Orleans.Sagas;

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

/// <summary>Nudges store readers after a commit; what the bundle dispatcher and the wake-up grains share.</summary>
internal static class LocalNudges
{
    /// <summary>
    /// Nudges every target for every partition, waking the readers a commit of <paramref name="eventTypeNames"/>
    /// concerns, or every reader without names. A nudge that cannot be sent — before the silo has started, while it
    /// stops — is a lost nudge like any other: the facts are committed and the poll reads them, so it is logged at debug
    /// with the consumers it was meant for and does not keep the others from being sent.
    /// </summary>
    public static async Task NudgeAsync(IEnumerable<INudgeTarget> targets, IGrainFactory grainFactory, IReadOnlyCollection<int> partitions, ILogger? logger, IReadOnlyCollection<string>? eventTypeNames = null)
    {
        var log = logger ?? NullLogger.Instance;
        foreach (var target in targets)
        {
            foreach (var partition in partitions)
            {
                try
                {
                    await target.NudgeAsync(grainFactory, partition, eventTypeNames);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (log.IsEnabled(LogLevel.Debug))
                    {
                        log.LogNudgeFailed(exception, string.Join(", ", target.ConsumerNames), partition);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Sends one nudge per consumer of <paramref name="consumers"/>. A nudge that cannot even be sent is collected rather
    /// than thrown at once, so one consumer the call fails for does not cost the others behind it their wake-up.
    /// </summary>
    /// <exception cref="AggregateException">A nudge could not be sent for one or more consumers.</exception>
    public static Task Send(IEnumerable<string> consumers, Func<string, Task> nudge)
    {
        List<Exception>? failures = null;
        foreach (var consumer in consumers)
        {
            try
            {
                nudge(consumer).Ignore();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                (failures ??= []).Add(exception);
            }
        }

        return failures is null ? Task.CompletedTask : Task.FromException(new AggregateException(failures));
    }
}

/// <summary>
/// The silo's side of a wake-up from a host that hosts no reader of the role: the host names the buckets its commit
/// touched, and the grain folds them into this silo's partitions and nudges the readers the silo registered for the
/// role, resolved once per activation. The host needs to know neither the readers' names nor the silo's partition
/// count. A silo that registers no reader of the role — one the placement could not filter — refuses the wake-up, so
/// the host logs it as lost rather than taking it as delivered.
/// </summary>
internal abstract class StoreWakeUpGrain<TTarget>(IServiceScopeFactory scopeFactory, IOptions<CommitOrderOptions> commitOrder, ILogger logger) : Grain
    where TTarget : INudgeTarget
{
    private readonly int _partitionCount = commitOrder.Value.PartitionCount;
    private IReadOnlyList<INudgeTarget> _targets = [];

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            _targets = TargetsOf(scope.ServiceProvider);
        }

        await base.OnActivateAsync(cancellationToken);
    }

    /// <summary>
    /// Wakes the readers the commit concerns, named by the event types its host put in the call's request context; a
    /// wake-up without them — from a host of an earlier version — wakes every reader. The names are taken out of the
    /// context, so the calls the woken readers make do not carry them on.
    /// </summary>
    public Task WakeAsync(int[] buckets) => WakeLocalAsync(_targets, GrainFactory, buckets, _partitionCount, logger, WakeUpEventTypes.Take());

    /// <summary>The readers of the role <paramref name="services"/> registers; their names are fixed when they are built.</summary>
    public static IReadOnlyList<INudgeTarget> TargetsOf(IServiceProvider services) =>
        [.. services.GetServices<INudgeTarget>().OfType<TTarget>()];

    /// <summary>
    /// Nudges, for every partition <paramref name="buckets"/> fall into under <paramref name="partitionCount"/>, every
    /// target's readers a commit of <paramref name="eventTypeNames"/> concerns, or all of them without names.
    /// </summary>
    /// <exception cref="InvalidOperationException">No reader of the role is registered on this silo.</exception>
    public static Task WakeLocalAsync(IReadOnlyList<INudgeTarget> targets, IGrainFactory grainFactory, int[] buckets, int partitionCount, ILogger logger, IReadOnlyCollection<string>? eventTypeNames = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(buckets);
        if (targets.Count == 0)
        {
            throw new InvalidOperationException(
                $"This silo registers no {typeof(TTarget).Name}, so the wake-up it was placed for reaches no reader; the readers of the role find the commit by their poll.");
        }

        var partitions = buckets.Select(bucket => PartitionMap.PartitionOf(bucket, partitionCount)).Distinct().ToList();
        return LocalNudges.NudgeAsync(targets, grainFactory, partitions, logger, eventTypeNames);
    }
}

/// <summary>
/// How a wake-up through the cluster names the event types of its commit: beside the call, in its request context, so
/// that the call itself is the one every version of the wake-up grain understands. A silo of an earlier version does
/// not read them and wakes every reader; a host of an earlier version does not set them, and every reader is woken.
/// </summary>
internal static class WakeUpEventTypes
{
    /// <summary>The request-context key the names travel under.</summary>
    public const string Key = "stratara.wake.event-types";

    /// <summary>
    /// Puts <paramref name="eventTypeNames"/> in the request context until the returned scope is disposed, which
    /// restores what the context held before.
    /// </summary>
    public static Scope Carry(IReadOnlyCollection<string> eventTypeNames)
    {
        var previous = RequestContext.Get(Key);
        RequestContext.Set(Key, eventTypeNames.ToArray());
        return new Scope(previous);
    }

    /// <summary>The names the call arrived with, taken out of the request context; <see langword="null"/> where it carried none.</summary>
    public static string[]? Take()
    {
        var names = RequestContext.Get(Key) as string[];
        RequestContext.Remove(Key);
        return names;
    }

    /// <summary>Restores the request context's earlier value for <see cref="Key"/>; the default scope restores nothing.</summary>
    public readonly struct Scope(object? previous) : IDisposable
    {
        private readonly bool _carried = true;

        public void Dispose()
        {
            if (!_carried)
            {
                return;
            }

            if (previous is null)
            {
                RequestContext.Remove(Key);
            }
            else
            {
                RequestContext.Set(Key, previous);
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
/// each is observed, and no more than <see cref="MaxOutstandingPerRole"/> are outstanding per role, so a silo that
/// hangs costs the host a bounded number of callbacks. A role whose grain cannot be placed because no silo of the
/// cluster hosts it is set aside for <see cref="StoreWakeUpOptions.AbsentRoleRetry"/> and then tried again; any other
/// failure is a lost wake-up.
/// </summary>
internal sealed class ClusterWakeUps
{
    /// <summary>How many wake-ups of one role may be awaiting their answer at once; a wake-up beyond it is lost.</summary>
    public const int MaxOutstandingPerRole = 256;

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

    /// <summary>
    /// Wakes the readers of the buckets a commit touched that a commit of <paramref name="eventTypeNames"/> concerns,
    /// naming the types beside each call, or every reader without names; returns at once.
    /// </summary>
    public void Wake(IGrainFactory grainFactory, IReadOnlyCollection<int> buckets, IReadOnlyCollection<string>? eventTypeNames = null)
    {
        if (_roles.Count == 0 || buckets.Count == 0)
        {
            return;
        }

        var byKey = buckets
            .GroupBy(bucket => PartitionMap.PartitionOf(bucket, _keys))
            .Select(group => (Key: group.Key, Buckets: group.ToArray()))
            .ToList();
        using (eventTypeNames is null ? default : WakeUpEventTypes.Carry(eventTypeNames))
        {
            foreach (var role in _roles)
            {
                role.Wake(grainFactory, byKey);
            }
        }
    }

    /// <summary>
    /// One role's wake-ups and whether they are set aside because no silo hosts the role. Every set-aside starts a new
    /// generation; an answer to a wake-up sent in an earlier one is stale and changes nothing, so calls in flight when
    /// the role was set aside neither end the set-aside nor log the role again.
    /// </summary>
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
        private int _generation;
        private int _outstanding;

        public ExecutionRole Role => role;

        private string Name => role.ToString().ToLowerInvariant();

        public void Wake(IGrainFactory grainFactory, IReadOnlyList<(int Key, int[] Buckets)> byKey)
        {
            int generation;
            lock (_gate)
            {
                if (timeProvider.GetUtcNow() < _setAsideUntil)
                {
                    return;
                }

                generation = _generation;
            }

            foreach (var (key, buckets) in byKey)
            {
                if (Interlocked.Increment(ref _outstanding) > MaxOutstandingPerRole)
                {
                    Interlocked.Decrement(ref _outstanding);
                    Lost(new InvalidOperationException($"{MaxOutstandingPerRole} wake-ups of the {Name} role are still awaiting their answer."), key);
                    continue;
                }

                Task sent;
                try
                {
                    sent = call(grainFactory, key, buckets);
                }
                catch (Exception exception)
                {
                    Interlocked.Decrement(ref _outstanding);
                    Failed(exception, key, generation);
                    continue;
                }

                sent.ContinueWith(
                    static (completed, state) =>
                    {
                        var (wakeUp, partition, sentIn) = ((RoleWakeUp, int, int))state!;
                        Interlocked.Decrement(ref wakeUp._outstanding);
                        if (completed.IsFaulted)
                        {
                            wakeUp.Failed(completed.Exception!.GetBaseException(), partition, sentIn);
                        }
                        else if (completed.IsCompletedSuccessfully)
                        {
                            wakeUp.Reached(sentIn);
                        }
                    },
                    (this, key, generation),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default).Ignore();
            }
        }

        private void Failed(Exception exception, int key, int sentIn)
        {
            if (!RolePlacement.NamesNoSiloFor(exception, role))
            {
                Lost(exception, key);
                return;
            }

            bool found;
            lock (_gate)
            {
                if (sentIn != _generation)
                {
                    return;
                }

                _setAsideUntil = timeProvider.GetUtcNow() + retry;
                _generation++;
                found = !_absent;
                _absent = true;
            }

            if (found && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogStoreWakeUpRoleAbsent(Name, retry);
            }
        }

        private void Reached(int sentIn)
        {
            lock (_gate)
            {
                if (sentIn != _generation || !_absent)
                {
                    return;
                }

                _absent = false;
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogStoreWakeUpRoleBack(Name);
            }
        }

        private void Lost(Exception exception, int key)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogNudgeFailed(exception, $"the {Name} role", key);
            }
        }
    }
}
