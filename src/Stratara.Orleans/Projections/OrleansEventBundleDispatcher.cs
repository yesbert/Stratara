using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Projections;
using Stratara.Contracts.Messages;
using Stratara.Orleans.CommitOrder;
using Stratara.Shared.Partitioning;

namespace Stratara.Orleans.Projections;

/// <summary>
/// The wake-up hint after a commit: instead of publishing the bundle, nudge the store-reading grains
/// of every target for the partitions the bundle touched whose consumer applies one of the bundle's events. The nudge is fire-and-forget and may be
/// lost; the store is the truth and the poll is the safety net, so a lost nudge costs latency, never
/// a fact. With an inner dispatcher the bundle is also published as before — the hybrid shape. With the store wake-ups
/// registered, the readers of every store-reading role this host does not host are woken through the cluster as well.
/// </summary>
internal sealed class OrleansEventBundleDispatcher(
    IGrainFactory grainFactory,
    IEnumerable<INudgeTarget> targets,
    IProjectionReplayState replayState,
    IOptions<CommitOrderOptions> commitOrder,
    ILogger<OrleansEventBundleDispatcher>? logger = null,
    InnerBundleDispatcher? innerDispatcher = null,
    ClusterWakeUps? clusterWakeUps = null) : IEventBundleOutboxDispatcher
{
    private readonly int _partitionCount = commitOrder.Value.PartitionCount;
    private readonly IEventBundleOutboxDispatcher? inner = innerDispatcher?.Dispatcher;

    /// <summary>The hybrid shape keeps the inner dispatcher's durability; the grain-only shape needs none, the store being the truth.</summary>
    public bool StoresBundlesWithCommit => inner?.StoresBundlesWithCommit ?? false;

    /// <summary>Whether a bundle can ever be stored for the drain: only the hybrid shape's inner dispatcher stores one.</summary>
    public bool StoresBundles => inner is not null;

    public Task StoreEventBundleAsync(EventBundle eventBundle, Stratara.Abstractions.Persistence.ITransaction transaction, CancellationToken cancellationToken = default) =>
        inner?.StoreEventBundleAsync(eventBundle, transaction, cancellationToken)
        ?? throw new NotSupportedException("The grain-only dispatcher does not store bundles with the commit; the store itself is what the grains read.");

    public async Task EnqueueEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventBundle);

        if (!replayState.IsReplayActive)
        {
            var buckets = eventBundle.Events.Select(e => BucketCalculator.GetBucketId(e.StreamId)).Distinct().ToList();
            var partitions = buckets.Select(bucket => PartitionMap.PartitionOf(bucket, _partitionCount)).Distinct().ToList();
            var eventTypeNames = eventBundle.Events.Select(e => e.EventTypeName).Distinct(StringComparer.Ordinal).ToList();
            await LocalNudges.NudgeAsync(targets, grainFactory, partitions, logger, eventTypeNames);
            clusterWakeUps?.Wake(grainFactory, buckets, eventTypeNames);
        }

        if (inner is not null)
        {
            await inner.EnqueueEventBundleAsync(eventBundle, cancellationToken);
        }
    }

    public Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default) =>
        inner?.EnqueueOutboxEntriesAsync(outboxEntries, cancellationToken) ?? Task.CompletedTask;
}
