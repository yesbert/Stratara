using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

namespace Stratara.Orleans.CommitOrder;

/// <summary>
/// The baseline every checkpoint reader in the framework uses today: entries with a sequence number
/// above the stored one. It keeps no promise about commit order — an entry that commits late with a
/// lower sequence number than one already returned is never seen again — and it exists so the tests
/// can show that happening.
/// </summary>
/// <typeparam name="TContext">The write context, with <see cref="CommitOrderModel"/> applied.</typeparam>
public sealed class NaiveSequenceReader<TContext>(IDbContextFactory<TContext> contextFactory, IOptions<CommitOrderOptions> options)
    : ICommittedPositionReader
    where TContext : DbContext, IWriteDbContext
{
    private readonly int _partitionCount = options.Value.PartitionCount;

    /// <inheritdoc/>
    public async Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var entries = await context.Set<EventStreamEntry>().AsNoTracking()
            .Where(e => e.SequenceNumber > afterPosition && e.BucketId % _partitionCount == partition)
            .OrderBy(e => e.SequenceNumber)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        return entries.Count == 0
            ? CommittedBatch.Empty(afterPosition)
            : new CommittedBatch([.. entries.Select(e => new CommittedEntry(e, e.SequenceNumber))], entries[^1].SequenceNumber);
    }
}
