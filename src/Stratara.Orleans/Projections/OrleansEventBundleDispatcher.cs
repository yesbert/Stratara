using Microsoft.Extensions.Options;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Projections;
using Stratara.Contracts.Messages;
using Stratara.Orleans.CommitOrder;
using Stratara.Shared.Partitioning;

namespace Stratara.Orleans.Projections;

/// <summary>
/// The wake-up hint after a commit: instead of publishing the bundle, nudge the store-reading grains
/// of every target for the partitions the bundle touched. The nudge is fire-and-forget and may be
/// lost; the store is the truth and the poll is the safety net, so a lost nudge costs latency, never
/// a fact. With an inner dispatcher the bundle is also published as before — the hybrid shape.
/// </summary>
internal sealed class OrleansEventBundleDispatcher(
    IGrainFactory grainFactory,
    IEnumerable<INudgeTarget> targets,
    IProjectionReplayState replayState,
    IOptions<CommitOrderOptions> commitOrder,
    InnerBundleDispatcher? innerDispatcher = null) : IEventBundleOutboxDispatcher
{
    private readonly int _partitionCount = commitOrder.Value.PartitionCount;
    private readonly IEventBundleOutboxDispatcher? inner = innerDispatcher?.Dispatcher;

    public async Task EnqueueEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventBundle);

        if (!replayState.IsReplayActive)
        {
            var partitions = eventBundle.Events
                .Select(e => PartitionMap.PartitionOf(BucketCalculator.GetBucketId(e.StreamId), _partitionCount))
                .Distinct()
                .ToList();
            foreach (var target in targets)
            {
                foreach (var partition in partitions)
                {
                    await target.NudgeAsync(grainFactory, partition);
                }
            }
        }

        if (inner is not null)
        {
            await inner.EnqueueEventBundleAsync(eventBundle, cancellationToken);
        }
    }

    public Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default) =>
        inner?.EnqueueOutboxEntriesAsync(outboxEntries, cancellationToken) ?? Task.CompletedTask;
}
