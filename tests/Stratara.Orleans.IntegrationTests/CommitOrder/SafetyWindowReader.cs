using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using Stratara.Abstractions.CommitOrder;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

namespace Stratara.Orleans.IntegrationTests.CommitOrder;

/// <summary>
/// The naive read with a delay: only entries older than <see cref="Window"/>
/// are returned, on the assumption that whatever was inserted before them has committed by now. A
/// transaction that stays open longer than the window breaks the assumption, which is why this is a
/// baseline and not a candidate.
/// </summary>
/// <typeparam name="TContext">A write context derived from the framework's write context.</typeparam>
public sealed class SafetyWindowReader<TContext>(
    IDbContextFactory<TContext> contextFactory,
    IOptions<CommitOrderOptions> options,
    TimeProvider timeProvider)
    : ICommittedPositionReader
    where TContext : DbContext, IWriteDbContext
{
    private readonly int _partitionCount = options.Value.PartitionCount;
    /// <summary>How much older than the moment of reading an entry must be before it is returned.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(100);

    /// <inheritdoc/>
    public async Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default)
    {
        var cutoff = timeProvider.GetUtcNow() - Window;
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
