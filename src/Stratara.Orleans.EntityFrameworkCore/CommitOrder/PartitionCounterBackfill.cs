using System.Globalization;
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
/// order the store's sequence numbered them — the closest to commit order a store without a commit record keeps —
/// except within a stream: a save does not number its entries in version order, so each stream's entries take the
/// places its sequence numbers hold in version order, and a batch is extended until no stream in it has an
/// unpositioned entry of a lower version beyond it. A position already handed out never changes, so a checkpoint
/// written before the backfill stays true and a reader resumes from it.
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

        var range = new Batch(partition, partitionCount, from, 0);
        if (await BoundAsync(context, range, cancellationToken) is not { } bound)
        {
            await transaction.CommitAsync(cancellationToken);
            return (0, from);
        }

        range = range with { To = bound };
        while (await FindStragglerAsync(context, range, cancellationToken) is { } straggler)
        {
            range = range with { To = straggler };
        }

        var count = statement is null
            ? await PositionEachAsync(context, range, counter, cancellationToken)
            : await PositionAllAsync(context, statement, range, counter, cancellationToken);

        await context.Set<PartitionPosition>()
            .Where(c => c.Partition == partition)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Position, counter + count), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (count, range.To);
    }

    /// <summary>The partition's entries without a position.</summary>
    private static IQueryable<EventStreamEntry> Unpositioned(DbContext context, Batch range) =>
        context.Set<EventStreamEntry>().AsNoTracking()
            .Where(e => e.BucketId % range.PartitionCount == range.Partition
                        && EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn) == null);

    /// <summary>The sequence number the batch ends at before it is extended, or <see langword="null"/> where nothing is left to position.</summary>
    private static Task<long?> BoundAsync(DbContext context, Batch range, CancellationToken cancellationToken) =>
        Unpositioned(context, range)
            .Where(e => e.SequenceNumber > range.From)
            .OrderBy(e => e.SequenceNumber)
            .Select(e => e.SequenceNumber)
            .Take(UpdateBatchSize)
            .MaxAsync(sequenceNumber => (long?)sequenceNumber, cancellationToken);

    /// <summary>
    /// The highest sequence number beyond the batch of an unpositioned entry whose stream appears in the batch at a
    /// higher version, or <see langword="null"/> where no stream of the batch continues below its top beyond it.
    /// </summary>
    private static Task<long?> FindStragglerAsync(DbContext context, Batch range, CancellationToken cancellationToken)
    {
        var tops = Unpositioned(context, range)
            .Where(e => e.SequenceNumber > range.From && e.SequenceNumber <= range.To)
            .GroupBy(e => new { e.BucketId, e.StreamId })
            .Select(g => new { g.Key.BucketId, g.Key.StreamId, Top = g.Max(e => e.Version) });

        return Unpositioned(context, range)
            .Where(e => e.SequenceNumber > range.To)
            .Join(tops,
                e => new { e.BucketId, e.StreamId },
                t => new { t.BucketId, t.StreamId },
                (e, t) => new { e.SequenceNumber, e.Version, t.Top })
            .Where(x => x.Version < x.Top)
            .MaxAsync(x => (long?)x.SequenceNumber, cancellationToken);
    }

    /// <summary>Positions the batch one entry at a time, on a provider the set-based statement is not written for.</summary>
    private static async Task<int> PositionEachAsync(DbContext context, Batch range, long counter, CancellationToken cancellationToken)
    {
        var slots = await Unpositioned(context, range)
            .Where(e => e.SequenceNumber > range.From && e.SequenceNumber <= range.To)
            .OrderBy(e => e.SequenceNumber)
            .Select(e => new { e.SequenceNumber, e.BucketId, e.StreamId, e.Version })
            .ToListAsync(cancellationToken);
        var versions = slots
            .GroupBy(e => (e.BucketId, e.StreamId))
            .ToDictionary(g => g.Key, g => new Queue<long>(g.OrderBy(e => e.Version).Select(e => e.SequenceNumber)));

        for (var i = 0; i < slots.Count; i++)
        {
            var sequenceNumber = versions[(slots[i].BucketId, slots[i].StreamId)].Dequeue();
            var position = counter + i + 1;
            await context.Set<EventStreamEntry>()
                .Where(e => e.SequenceNumber == sequenceNumber)
                .ExecuteUpdateAsync(set => set.SetProperty(e => EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn), position), cancellationToken);
        }

        return slots.Count;
    }

    /// <summary>Positions the batch with one statement on PostgreSQL, and reads back how many it positioned.</summary>
    private static async Task<int> PositionAllAsync(DbContext context, string statement, Batch range, long counter, CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = statement;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = range.From });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = range.To });
        command.Parameters.Add(new NpgsqlParameter("partitions", NpgsqlTypes.NpgsqlDbType.Integer) { Value = range.PartitionCount });
        command.Parameters.Add(new NpgsqlParameter("partition", NpgsqlTypes.NpgsqlDbType.Integer) { Value = range.Partition });
        command.Parameters.Add(new NpgsqlParameter("counter", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = counter });

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
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

    /// <summary>One batch of a partition: the unpositioned entries with a sequence number after <see cref="From"/> and up to <see cref="To"/>.</summary>
    private sealed record Batch(int Partition, int PartitionCount, long From, long To);

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
            var stream = Column(nameof(EventStreamEntry.StreamId));
            var version = Column(nameof(EventStreamEntry.Version));
            var position = Column(CommitOrderSchema.PartitionPositionColumn);

            return $$"""
                WITH batch AS (
                    SELECT {{sequence}}, {{bucket}}, {{stream}},
                           row_number() OVER (ORDER BY {{sequence}}) AS ordinal,
                           row_number() OVER (PARTITION BY {{bucket}}, {{stream}} ORDER BY {{sequence}}) AS slot,
                           row_number() OVER (PARTITION BY {{bucket}}, {{stream}} ORDER BY {{version}}) AS version_rank
                    FROM {{target}}
                    WHERE {{sequence}} > @from AND {{sequence}} <= @to AND {{bucket}} % @partitions = @partition AND {{position}} IS NULL
                ), placed AS (
                    SELECT by_version.{{sequence}} AS placed_sequence, by_slot.ordinal
                    FROM batch AS by_version
                    JOIN batch AS by_slot
                      ON by_slot.{{bucket}} = by_version.{{bucket}} AND by_slot.{{stream}} = by_version.{{stream}} AND by_slot.slot = by_version.version_rank
                ), positioned AS (
                    UPDATE {{target}} AS entry SET {{position}} = @counter + placed.ordinal
                    FROM placed
                    WHERE entry.{{sequence}} = placed.placed_sequence
                    RETURNING entry.{{sequence}}
                )
                SELECT count(*) FROM positioned
                """;
        }
    }
}
