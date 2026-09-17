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
/// Every process that appends to the store must maintain the counter, and the partition count must not change
/// without renumbering the entries, which the framework does not offer. An entry written without the interceptor
/// carries no position; rather than read past it, a read of its partition fails naming it until the entry is
/// positioned with <see cref="PartitionCounterBackfill"/>. The reader's name carries the partition count, because
/// a position is only meaningful under the count its partition was counted with. Verified on PostgreSQL only.
/// </remarks>
/// <typeparam name="TContext">A write context derived from the framework's write context.</typeparam>
public sealed class PortableCounterReader<TContext>(IDbContextFactory<TContext> contextFactory, IOptions<CommitOrderOptions> options)
    : ICommittedPositionReader
    where TContext : DbContext, IWriteDbContext
{
    /// <summary>How many unpositioned entries one read looks at to find one of its partition.</summary>
    private const int UnpositionedProbe = 64;

    private readonly int _partitionCount = options.Value.PartitionCount;

    /// <inheritdoc/>
    public string Name => $"partition-counter/{_partitionCount}";

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">An entry of <paramref name="partition"/> was appended without a position.</exception>
    public async Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await RefuseUnpositionedAsync(context, partition, cancellationToken);

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

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">An entry of <paramref name="partition"/> was appended without a position.</exception>
    public async Task<long> HeadAsync(int partition, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await RefuseUnpositionedAsync(context, partition, cancellationToken);
        var head = await context.Set<EventStreamEntry>().AsNoTracking()
            .Where(e => e.BucketId % _partitionCount == partition)
            .MaxAsync(e => EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn), cancellationToken);
        return head.GetValueOrDefault();
    }

    /// <summary>
    /// Looks up unpositioned entries through the position index — the partition is derived in memory, because a
    /// filter on the bucket cannot use it — and refuses to read a partition that holds one.
    /// </summary>
    /// <exception cref="InvalidOperationException">An entry of the partition was appended without a position.</exception>
    private async Task RefuseUnpositionedAsync(TContext context, int partition, CancellationToken cancellationToken)
    {
        var unpositioned = await context.Set<EventStreamEntry>().AsNoTracking()
            .Where(e => EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn) == null)
            .Select(e => new { e.SequenceNumber, e.BucketId })
            .Take(UnpositionedProbe)
            .ToListAsync(cancellationToken);

        var blocking = unpositioned.FirstOrDefault(e => PartitionMap.PartitionOf(e.BucketId, _partitionCount) == partition);
        if (blocking is not null)
        {
            throw new InvalidOperationException(
                $"Entry {blocking.SequenceNumber} of partition {partition} has no partition position, so the portable commit-order reader stops before it instead of reading past it. A process appended it without {nameof(PartitionCounterInterceptor)}; add the interceptor to every write context that appends to this store, then position the entry with {nameof(PartitionCounterBackfill)}.{nameof(PartitionCounterBackfill.RunAsync)}.");
        }
    }
}
