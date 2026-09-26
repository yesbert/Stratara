using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;
using Stratara.Shared.EventSourcing;
using Stratara.Shared.Partitioning;
using Stratara.Shared.Reflections;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// Default <see cref="ISnapshotService"/> that delegates the snapshot decision to the registered
/// <see cref="ISnapshotStrategy"/> and persists a fresh snapshot whenever the strategy approves.
/// </summary>
/// <remarks>
/// Snapshots are protected under the stream's recorded owner — the tenant and user of its first
/// entry — and persisted through <see cref="ISecureJsonSerializer"/>, so an erasure that reaches the
/// stream's events reaches its snapshots too. It is called once the batch is committed, and builds the
/// snapshot from the committed stream up to the batch's highest version, so it never captures an event
/// that was not recorded. The cadence (and whether snapshots run at all) is owned entirely by the
/// injected <see cref="ISnapshotStrategy"/>; the default <see cref="VersionThresholdSnapshotStrategy"/>
/// snapshots every 50 versions, and <see cref="NoSnapshotStrategy"/> turns snapshotting off.
/// </remarks>
internal sealed class SnapshotService(
    IAggregationService aggregationService,
    ISecureJsonSerializer serializer,
    IWriteUnitOfWork unitOfWork,
    ITrustedTypeResolver typeResolver,
    ISnapshotStrategy snapshotStrategy) : ISnapshotService
{
    /// <inheritdoc/>
    public async Task AddSnapshotIfNeededAsync(IEnumerable<EventStreamEntry> eventStreamEntries, CancellationToken cancellationToken = default)
    {
        var batch = eventStreamEntries.ToList();
        var streamGroups = batch.GroupBy(x => (x.StreamId, TypeKey: x.AggregateTypeName.GetVersionIndependentTypeName()));
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var snapshotRepository = unitOfWork.CreateSnapshotRepository(transaction);
        var eventStreamRepository = unitOfWork.CreateEventStreamRepository(transaction);

        foreach (var streamGroup in streamGroups)
        {
            var streamId = streamGroup.Key.StreamId;
            var streamEntries = streamGroup.ToList();
            if (!await ShouldCreateSnapshot(snapshotRepository, streamId, streamEntries, cancellationToken))
            {
                continue;
            }

            var owner = await ResolveStreamOwnerAsync(eventStreamRepository, streamId, batch, cancellationToken);
            var aggregatedEvent = await CreateSnapshot(streamId, streamEntries, owner, cancellationToken);
            await snapshotRepository.AddAsync(aggregatedEvent, cancellationToken);
        }

        await transaction.SaveChangesAsync(cancellationToken);
    }

    /// <remarks>
    /// Takes the caller's repository rather than opening its own transaction. It used to open one per
    /// stream, on the write hot path, to answer a single version lookup the caller's transaction could
    /// already serve.
    /// </remarks>
    private async Task<bool> ShouldCreateSnapshot(
        ISnapshotRepository snapshotRepository,
        Guid streamId,
        List<EventStreamEntry> streamEntries,
        CancellationToken cancellationToken)
    {
        if (streamEntries.Count == 0)
        {
            return false;
        }

        var currentVersion = streamEntries.Max(x => x.Version);
        var aggregateTypeName = streamEntries[0].AggregateTypeName;
        var snapshotVersion = await snapshotRepository.GetLatestVersionOrDefaultAsync(streamId, aggregateTypeName, cancellationToken);
        var aggregateType = typeResolver.Resolve(aggregateTypeName);

        return snapshotStrategy.ShouldSnapshot(aggregateType, currentVersion, snapshotVersion);
    }

    /// <remarks>
    /// The stream's owner is recorded on its first entry. When this batch creates the stream that entry
    /// is in the batch — possibly under another aggregate type than the one being snapshotted — and not
    /// committed yet; otherwise it is read. The batch's own first entry for the stream is not used: it
    /// may carry a Subject stated for that one event, which is not the stream's owner.
    /// </remarks>
    private static async Task<EventSubject> ResolveStreamOwnerAsync(
        IEventStreamRepository eventStreamRepository,
        Guid streamId,
        List<EventStreamEntry> batch,
        CancellationToken cancellationToken)
    {
        var firstEntry = batch.Find(entry => entry.StreamId == streamId && entry.Version == 1)
                         ?? await eventStreamRepository.GetFirstOrDefaultAsync(streamId, cancellationToken)
                         ?? batch.First(entry => entry.StreamId == streamId);
        return new EventSubject(firstEntry.TenantId, firstEntry.UserId);
    }

    private async Task<Snapshot> CreateSnapshot(Guid streamId, List<EventStreamEntry> streamEntries, EventSubject owner,
        CancellationToken cancellationToken)
    {
        var currentVersion = streamEntries.Max(x => x.Version);
        var aggregateTypeName = streamEntries[0].AggregateTypeName;
        var type = typeResolver.Resolve(aggregateTypeName);
        var aggregate = await aggregationService.AggregateAsync(type, streamId, toVersion: currentVersion, cancellationToken: cancellationToken)
                        ?? ObjectFactory.CreateInstance(type);

        var dataJson = await serializer.SerializeAsync(aggregate, owner.TenantId, owner.UserId, cancellationToken);
        return new Snapshot
        {
            Id = Guid.CreateVersion7(),
            BucketId = BucketCalculator.GetBucketId(streamId),
            StreamId = streamId,
            Version = currentVersion,
            AggregateTypeName = aggregateTypeName,
            DataJson = dataJson,
            TenantId = owner.TenantId,
            UserId = owner.UserId,
            Timestamp = DateTimeOffset.UtcNow
        };
    }
}