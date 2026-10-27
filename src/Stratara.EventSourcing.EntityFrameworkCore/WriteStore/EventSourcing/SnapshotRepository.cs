using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.EventSourcing;
using Stratara.Shared.Partitioning;
using Stratara.Shared.Reflections;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing;

/// <summary>
/// EF Core-backed <see cref="ISnapshotRepository"/> over the <c>snapshot</c> table. Reads
/// return the latest snapshot at or before a target version; new snapshots are inserted via
/// the surrounding unit of work. Reads see every tenant's snapshots, whatever query filters the
/// context declares, as the reads of the stream they shorten do.
/// </summary>
/// <param name="context">The write-store DbContext that hosts the snapshot table.</param>
internal sealed class SnapshotRepository(IWriteDbContext context) : ISnapshotRepository
{
    /// <inheritdoc/>
    public Task<Snapshot?> GetAsync(Guid streamId, string aggregateTypeName, long? toVersion = null, CancellationToken cancellationToken = default) =>
        StreamSnapshots(streamId, aggregateTypeName)
            .Where(e => !toVersion.HasValue || e.Version <= toVersion)
            .OrderByDescending(e => e.Version)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task<long> GetLatestVersionOrDefaultAsync(Guid streamId, string aggregateTypeName, CancellationToken cancellationToken = default) =>
        await StreamSnapshots(streamId, aggregateTypeName)
            .OrderByDescending(e => e.Version)
            .Select(e => (long?)e.Version)
            .FirstOrDefaultAsync(cancellationToken) ?? 0L;

    /// <summary>
    /// The snapshots of one stream for one aggregate type, whatever its assembly version, within the partition the
    /// stream's identifier gives it, so that the unique index over partition, stream and version serves the read.
    /// </summary>
    private IQueryable<Snapshot> StreamSnapshots(Guid streamId, string aggregateTypeName)
    {
        var bucketId = BucketCalculator.GetBucketId(streamId);
        var versionIndependentName = aggregateTypeName.GetVersionIndependentTypeName();
        var prefix = versionIndependentName + ",";
        return context.Set<Snapshot>().AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.BucketId == bucketId && e.StreamId == streamId
                        && (e.AggregateTypeName == versionIndependentName || e.AggregateTypeName.StartsWith(prefix)));
    }

    /// <inheritdoc/>
    public async Task AddAsync(Snapshot snapshot, CancellationToken cancellationToken = default)
    {
        await context.Set<Snapshot>().AddAsync(snapshot, cancellationToken);
    }
}
