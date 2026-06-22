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
/// Snapshots are tenant-scoped and persisted through <see cref="ISecureJsonSerializer"/> (tenant
/// AAD). The cadence (and whether snapshots run at all) is owned entirely by the injected
/// <see cref="ISnapshotStrategy"/>; the default <see cref="VersionThresholdSnapshotStrategy"/>
/// snapshots every 50 versions, and <see cref="NoSnapshotStrategy"/> turns snapshotting off.
/// </remarks>
internal sealed class SnapshotService(
    IAggregationService aggregationService,
    IEventMapperFactory eventMapperFactory,
    ISecureJsonSerializer serializer,
    IWriteUnitOfWork unitOfWork,
    ITrustedTypeResolver typeResolver,
    ISnapshotStrategy snapshotStrategy) : ISnapshotService
{
    /// <inheritdoc/>
    public async Task AddSnapshotIfNeededAsync(IEnumerable<EventStreamEntry> eventStreamEntries, CancellationToken cancellationToken = default)
    {
        var streamGroups = eventStreamEntries.GroupBy(x => x.StreamId);
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var snapshotRepository = unitOfWork.CreateSnapshotRepository(transaction);

        foreach (var streamGroup in streamGroups)
        {
            var streamId = streamGroup.Key;
            var streamEntries = streamGroup.ToList();
            if (!await ShouldCreateSnapshot(streamId, streamEntries, cancellationToken))
            {
                continue;
            }

            var aggregatedEvent = await CreateSnapshot(streamId, streamGroup.ToList(), cancellationToken);
            await snapshotRepository.AddAsync(aggregatedEvent, cancellationToken);
        }

        await transaction.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> ShouldCreateSnapshot(Guid streamId, List<EventStreamEntry> streamEntries, CancellationToken cancellationToken)
    {
        if (streamEntries.Count == 0)
        {
            return false;
        }

        var currentVersion = streamEntries.Max(x => x.Version);
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var snapshotRepository = unitOfWork.CreateSnapshotRepository(transaction);
        var snapshotVersion = await snapshotRepository.GetLatestVersionOrDefaultAsync(streamId, cancellationToken);
        var aggregateType = typeResolver.Resolve(streamEntries[0].AggregateTypeName);

        return snapshotStrategy.ShouldSnapshot(aggregateType, currentVersion, snapshotVersion);
    }

    private async Task<Snapshot> CreateSnapshot(Guid streamId, List<EventStreamEntry> streamEntries, CancellationToken cancellationToken)
    {
        var currentVersion = streamEntries.Max(x => x.Version);
        var firstEntry = streamEntries[0];
        var subjectTenantId = firstEntry.TenantId;
        var aggregateTypeName = firstEntry.AggregateTypeName;
        var type = typeResolver.Resolve(aggregateTypeName);
        var aggregate = await aggregationService.AggregateAsync(type, streamId, cancellationToken: cancellationToken)
                        ?? ObjectFactory.CreateInstance(type);
        var events = await eventMapperFactory.MapToEventsAsync(streamEntries, cancellationToken);
        aggregate.ApplyEvents(events);

        var dataJson = await serializer.SerializeAsync(aggregate, subjectTenantId, cancellationToken: cancellationToken);
        return new Snapshot
        {
            Id = Guid.CreateVersion7(),
            BucketId = BucketCalculator.GetBucketId(streamId),
            StreamId = streamId,
            Version = currentVersion,
            AggregateTypeName = aggregateTypeName,
            DataJson = dataJson,
            TenantId = subjectTenantId,
            Timestamp = DateTimeOffset.UtcNow
        };
    }
}