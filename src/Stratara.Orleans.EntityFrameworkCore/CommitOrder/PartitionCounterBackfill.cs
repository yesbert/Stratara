using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
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

        var statement = context.Database.IsNpgsql() ? Statement.For(context) : null;
        var positioned = 0;
        for (var partition = 0; partition < options.PartitionCount; partition++)
        {
            positioned += await RunPartitionAsync(context, statement, partition, options.PartitionCount, cancellationToken);
        }

        return positioned;
    }

    /// <summary>
    /// Positions a partition's entries in batches, each under the partition's counter lock and a transaction of its
    /// own, starting where the batch before it ended: a store with a long history is walked once rather than once per
    /// batch, and the counter — with the appends waiting behind it — is held for a batch rather than for the run. An
    /// entry appended between two batches is positioned before the entries the next batch positions, so a stream
    /// written to while it is repaired is read out of order and its read model rebuilt, which is what repairing an
    /// unpositioned entry already costs.
    /// </summary>
    private static async Task<int> RunPartitionAsync(DbContext context, string? statement, int partition, int partitionCount, CancellationToken cancellationToken)
    {
        var positioned = 0;
        var from = 0L;
        while (true)
        {
            var batch = await PositionBatchAsync(context, statement, partition, partitionCount, from, cancellationToken);
            if (batch.Count == 0)
            {
                return positioned;
            }

            positioned += batch.Count;
            from = batch.Last;
        }
    }

    /// <summary>
    /// Positions the oldest entries after <paramref name="from"/> that have none, and says where it ended — so the
    /// batch after it starts there instead of walking everything this one positioned.
    /// </summary>
    private static async Task<(int Count, long Last)> PositionBatchAsync(DbContext context, string? statement, int partition, int partitionCount, long from, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var counter = await LockCounterAsync(context, partition, cancellationToken);

        var (count, last) = statement is null
            ? await PositionEachAsync(context, partition, partitionCount, from, counter, cancellationToken)
            : await PositionAllAsync(context, statement, partition, partitionCount, from, counter, cancellationToken);
        if (count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return (0, from);
        }

        await context.Set<PartitionPosition>()
            .Where(c => c.Partition == partition)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Position, counter + count), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (count, last);
    }

    /// <summary>Positions the batch one entry at a time, on a provider the set-based statement is not written for.</summary>
    private static async Task<(int Count, long Last)> PositionEachAsync(DbContext context, int partition, int partitionCount, long from, long counter, CancellationToken cancellationToken)
    {
        var unpositioned = await context.Set<EventStreamEntry>()
            .Where(e => e.SequenceNumber > from
                        && e.BucketId % partitionCount == partition
                        && EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn) == null)
            .OrderBy(e => e.SequenceNumber)
            .Select(e => e.SequenceNumber)
            .Take(UpdateBatchSize)
            .ToListAsync(cancellationToken);
        if (unpositioned.Count == 0)
        {
            return (0, from);
        }

        for (var i = 0; i < unpositioned.Count; i++)
        {
            var sequenceNumber = unpositioned[i];
            var position = counter + i + 1;
            await context.Set<EventStreamEntry>()
                .Where(e => e.SequenceNumber == sequenceNumber)
                .ExecuteUpdateAsync(set => set.SetProperty(e => EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn), position), cancellationToken);
        }

        return (unpositioned.Count, unpositioned[^1]);
    }

    /// <summary>Positions the batch with one statement on PostgreSQL, and reads back how many it positioned and the last of them.</summary>
    private static async Task<(int Count, long Last)> PositionAllAsync(DbContext context, string statement, int partition, int partitionCount, long from, long counter, CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = statement;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = from });
        command.Parameters.Add(new NpgsqlParameter("partitions", NpgsqlTypes.NpgsqlDbType.Integer) { Value = partitionCount });
        command.Parameters.Add(new NpgsqlParameter("partition", NpgsqlTypes.NpgsqlDbType.Integer) { Value = partition });
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlTypes.NpgsqlDbType.Integer) { Value = UpdateBatchSize });
        command.Parameters.Add(new NpgsqlParameter("counter", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = counter });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var count = reader.GetInt64(0);
        return count == 0 ? (0, from) : ((int)count, reader.GetInt64(1));
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

    /// <summary>The set-based positioning statement, with the tables and columns named as the context's model maps them.</summary>
    private static class Statement
    {
        /// <exception cref="InvalidOperationException">The model maps no event stream table, or lacks a column the statement names.</exception>
        public static string For(DbContext context)
        {
            var entity = context.Model.FindEntityType(typeof(EventStreamEntry))
                         ?? throw new InvalidOperationException($"The model of {context.GetType().Name} has no {nameof(EventStreamEntry)}.");
            var tableName = entity.GetTableName()
                            ?? throw new InvalidOperationException($"{nameof(EventStreamEntry)} is not mapped to a table in {context.GetType().Name}.");
            var table = StoreObjectIdentifier.Table(tableName, entity.GetSchema());
            var sql = context.GetService<ISqlGenerationHelper>();

            string Column(string property)
            {
                var name = entity.FindProperty(property)?.GetColumnName(table)
                           ?? throw new InvalidOperationException($"{nameof(EventStreamEntry)}.{property} is not mapped in {context.GetType().Name}; the framework's write context declares it.");
                return sql.DelimitIdentifier(name);
            }

            var target = sql.DelimitIdentifier(tableName, entity.GetSchema());
            var sequence = Column(nameof(EventStreamEntry.SequenceNumber));
            var bucket = Column(nameof(EventStreamEntry.BucketId));
            var position = Column(CommitOrderSchema.PartitionPositionColumn);

            return $$"""
                WITH batch AS (
                    SELECT {{sequence}}, row_number() OVER (ORDER BY {{sequence}}) AS ordinal
                    FROM (
                        SELECT {{sequence}} FROM {{target}}
                        WHERE {{sequence}} > @from AND {{bucket}} % @partitions = @partition AND {{position}} IS NULL
                        ORDER BY {{sequence}}
                        LIMIT @limit
                    ) AS unpositioned
                ), positioned AS (
                    UPDATE {{target}} AS entry SET {{position}} = @counter + batch.ordinal
                    FROM batch
                    WHERE entry.{{sequence}} = batch.{{sequence}}
                    RETURNING entry.{{sequence}}
                )
                SELECT count(*), max({{sequence}}) FROM positioned
                """;
        }
    }
}
