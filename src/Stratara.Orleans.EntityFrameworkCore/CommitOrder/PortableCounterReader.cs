using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using Stratara.Orleans.CommitOrder;

namespace Stratara.Orleans.EntityFrameworkCore.CommitOrder;

/// <summary>
/// Reads by the partition position that <see cref="PartitionCounterInterceptor"/> stamps on every
/// entry. Because the counter row is locked from the increment to the commit, positions in a
/// partition are handed out and committed in the same order, so an entry with a lower position than
/// one already seen has already committed — the promise of the port, on any relational database.
/// </summary>
/// <remarks>
/// Entries written without the interceptor carry no position and are invisible to this reader; a
/// store that adopts the counter backfills them once. The reader's name carries the partition count,
/// because a position is only meaningful under the count its partition was counted with.
/// </remarks>
/// <typeparam name="TContext">A write context derived from the framework's write context.</typeparam>
public sealed class PortableCounterReader<TContext>(IDbContextFactory<TContext> contextFactory, IOptions<CommitOrderOptions> options)
    : ICommittedPositionReader
    where TContext : DbContext, IWriteDbContext
{
    private readonly int _partitionCount = options.Value.PartitionCount;

    /// <inheritdoc/>
    public string Name => $"partition-counter/{_partitionCount}";

    /// <inheritdoc/>
    public async Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var entries = await context.Set<EventStreamEntry>().AsNoTracking()
            .Where(e => e.BucketId % _partitionCount == partition
                        && EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn) > afterPosition)
            .OrderBy(e => EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn))
            .Select(e => new { Entry = e, Position = EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn) })
            .Take(batchSize + 1)
            .ToListAsync(cancellationToken);

        if (entries.Count == 0)
        {
            return CommittedBatch.Empty(afterPosition);
        }

        var hasMore = entries.Count > batchSize;
        var batch = hasMore ? entries[..batchSize] : entries;
        return new CommittedBatch([.. batch.Select(e => new CommittedEntry(e.Entry, e.Position.GetValueOrDefault()))], batch[^1].Position.GetValueOrDefault())
        {
            HasMore = hasMore,
        };
    }
}
