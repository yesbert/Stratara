using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

namespace Stratara.Orleans.CommitOrder;

/// <summary>
/// The naive read with a delay: only entries older than <see cref="CommitOrderOptions.SafetyWindow"/>
/// are returned, on the assumption that whatever was inserted before them has committed by now. A
/// transaction that stays open longer than the window breaks the assumption, which is why this is a
/// baseline and not a candidate.
/// </summary>
/// <typeparam name="TContext">The write context, with <see cref="CommitOrderModel"/> applied.</typeparam>
public sealed class SafetyWindowReader<TContext>(
    IDbContextFactory<TContext> contextFactory,
    IOptions<CommitOrderOptions> options,
    TimeProvider timeProvider)
    : ICommittedPositionReader
    where TContext : DbContext, IWriteDbContext
{
    private readonly int _partitionCount = options.Value.PartitionCount;
    private readonly TimeSpan _window = options.Value.SafetyWindow;

    /// <inheritdoc/>
    public async Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default)
    {
        var cutoff = timeProvider.GetUtcNow() - _window;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var entries = await context.Set<EventStreamEntry>().AsNoTracking()
            .Where(e => e.SequenceNumber > afterPosition && e.BucketId % _partitionCount == partition && e.Timestamp <= cutoff)
            .OrderBy(e => e.SequenceNumber)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        return entries.Count == 0
            ? CommittedBatch.Empty(afterPosition)
            : new CommittedBatch([.. entries.Select(e => new CommittedEntry(e, e.SequenceNumber))], entries[^1].SequenceNumber);
    }
}
