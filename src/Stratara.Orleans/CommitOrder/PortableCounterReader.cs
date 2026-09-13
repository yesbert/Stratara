using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

namespace Stratara.Orleans.CommitOrder;

/// <summary>
/// Reads by the partition position that <see cref="PartitionCounterInterceptor"/> stamps on every
/// entry. Because the counter row is locked from the increment to the commit, positions in a
/// partition are handed out and committed in the same order, so an entry with a lower position than
/// one already seen has already committed — the promise of the port, on any relational database.
/// </summary>
/// <remarks>
/// Entries written without the interceptor carry no position and are invisible to this reader; a
/// store that adopts the counter backfills them once.
/// </remarks>
/// <typeparam name="TContext">The write context, with <see cref="CommitOrderModel"/> applied.</typeparam>
public sealed class PortableCounterReader<TContext>(IDbContextFactory<TContext> contextFactory, IOptions<CommitOrderOptions> options)
    : ICommittedPositionReader
    where TContext : DbContext, IWriteDbContext
{
    private readonly int _partitionCount = options.Value.PartitionCount;

    /// <inheritdoc/>
    public async Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var entries = await context.Set<EventStreamEntry>().AsNoTracking()
            .Where(e => e.BucketId % _partitionCount == partition
                        && EF.Property<long?>(e, CommitOrderModel.PartitionPositionColumn) > afterPosition)
            .OrderBy(e => EF.Property<long?>(e, CommitOrderModel.PartitionPositionColumn))
            .Select(e => new { Entry = e, Position = EF.Property<long?>(e, CommitOrderModel.PartitionPositionColumn) })
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        return entries.Count == 0
            ? CommittedBatch.Empty(afterPosition)
            : new CommittedBatch([.. entries.Select(e => new CommittedEntry(e.Entry, e.Position!.Value))], entries[^1].Position!.Value);
    }
}
