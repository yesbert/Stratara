using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using Stratara.Orleans.CommitOrder;

namespace Stratara.Orleans.EntityFrameworkCore.CommitOrder;

/// <summary>
/// Positions the entries a store holds from before it adopted the partition counter, once, so the
/// portable reader can serve it. Each partition is positioned in a transaction of its own that holds
/// the partition's counter row, so appends to that partition wait for it and no position is handed
/// out twice. Entries without a position take the positions after the last one the partition handed out, in the
/// order the store's sequence numbered them — the closest to commit order a store without a commit record keeps.
/// A position already handed out never changes, so a checkpoint written before the backfill stays true and a reader
/// resumes from it.
/// </summary>
/// <remarks>
/// Run it once after migrating and before the portable reader's host starts; on a store no process has appended to
/// with the counter yet, history is positioned from the first position. Running it again on a positioned store
/// changes nothing. An entry appended without the counter after entries appended with it is read after them — a later
/// version of its own stream included — so stop the process that appends without the counter before running the
/// backfill; a read model that stops on the resulting order is repaired by rebuilding it.
/// </remarks>
public static class PartitionCounterBackfill
{
    private const int UpdateBatchSize = 1_000;

    /// <summary>Positions every unpositioned entry of every partition and seeds each partition's counter.</summary>
    /// <param name="context">A context derived from the framework's write context.</param>
    /// <param name="options">The commit-order settings the readers and the interceptor run with.</param>
    /// <param name="cancellationToken">Cancels between partitions and between update batches.</param>
    /// <returns>How many entries were positioned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The partition count is not positive.</exception>
    public static async Task<int> RunAsync(DbContext context, CommitOrderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PartitionCount);

        var positioned = 0;
        for (var partition = 0; partition < options.PartitionCount; partition++)
        {
            positioned += await RunPartitionAsync(context, partition, options.PartitionCount, cancellationToken);
        }

        return positioned;
    }

    private static async Task<int> RunPartitionAsync(DbContext context, int partition, int partitionCount, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var counter = await LockCounterAsync(context, partition, cancellationToken);

        var unpositioned = await context.Set<EventStreamEntry>()
            .Where(e => e.BucketId % partitionCount == partition && EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn) == null)
            .OrderBy(e => e.SequenceNumber)
            .Select(e => e.SequenceNumber)
            .ToListAsync(cancellationToken);
        if (unpositioned.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        for (var offset = 0; offset < unpositioned.Count; offset += UpdateBatchSize)
        {
            var batch = unpositioned.Skip(offset).Take(UpdateBatchSize).ToList();
            for (var i = 0; i < batch.Count; i++)
            {
                var sequenceNumber = batch[i];
                var position = counter + offset + i + 1;
                await context.Set<EventStreamEntry>()
                    .Where(e => e.SequenceNumber == sequenceNumber)
                    .ExecuteUpdateAsync(set => set.SetProperty(e => EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn), position), cancellationToken);
            }
        }

        await context.Set<PartitionPosition>()
            .Where(c => c.Partition == partition)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Position, counter + unpositioned.Count), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return unpositioned.Count;
    }

    /// <summary>
    /// Takes the partition's counter row for the rest of the transaction, creating it where the store
    /// has none yet, and returns the last position it handed out.
    /// </summary>
    private static async Task<long> LockCounterAsync(DbContext context, int partition, CancellationToken cancellationToken)
    {
        var locked = await context.Set<PartitionPosition>()
            .Where(c => c.Partition == partition)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Position, c => c.Position), cancellationToken);
        if (locked == 0)
        {
            context.Set<PartitionPosition>().Add(new PartitionPosition { Partition = partition, Position = 0 });
            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();
        }

        return await context.Set<PartitionPosition>().AsNoTracking()
            .Where(c => c.Partition == partition)
            .Select(c => c.Position)
            .SingleAsync(cancellationToken);
    }
}
