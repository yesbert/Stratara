using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Orleans.CommitOrder;

namespace Stratara.Orleans.EntityFrameworkCore.CommitOrder;

/// <summary>
/// PostgreSQL only: orders by the transaction id the database stamped on each entry and returns only
/// entries whose transaction is older than every transaction still in progress, as the reading
/// statement's own snapshot sees it. Such a transaction has ended, and if it committed its entries
/// are visible to that same snapshot — so nothing at or below the returned position can still
/// appear.
/// </summary>
/// <remarks>
/// <para>
/// The position is the transaction id, not the sequence number. The two are assigned in separate
/// steps, so a transaction can hold the lower id and the higher sequence number; a reader that
/// orders by sequence number and only filters by transaction id can still skip. Ordering by id makes
/// the filter and the order agree.
/// </para>
/// <para>
/// A transaction writes all its entries under one id, so a batch never ends in the middle of one:
/// the entries of the last id are returned whole or held for the next batch.
/// </para>
/// <para>
/// A long-running transaction that never writes an entry still holds the horizon back, so this
/// reader delays rather than skips under one.
/// </para>
/// <para>
/// The table and column names come from the context's model, so a model that does not follow the
/// snake-case convention is read the same way.
/// </para>
/// </remarks>
/// <typeparam name="TContext">A write context derived from the framework's write context on PostgreSQL.</typeparam>
public sealed class PostgresTransactionIdReader<TContext>(IDbContextFactory<TContext> contextFactory, IOptions<CommitOrderOptions> options)
    : ICommittedPositionReader
    where TContext : DbContext, IWriteDbContext
{
    private readonly int _partitionCount = options.Value.PartitionCount;
    private Statements? _statements;

    /// <inheritdoc/>
    public async Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var statements = _statements ??= Statements.For(context);
        var after = ((ulong)afterPosition).ToString(CultureInfo.InvariantCulture);

        var projected = await context.Set<EventStreamEntry>()
            .FromSqlRaw(statements.ReadAfter, _partitionCount, partition, after, batchSize + 1)
            .AsNoTracking()
            .Select(e => new { Entry = e, TransactionId = EF.Property<ulong>(e, CommitOrderSchema.TransactionIdColumn) })
            .ToListAsync(cancellationToken);
        var rows = projected.Select(row => new Row(row.Entry, row.TransactionId)).ToList();

        if (rows.Count == 0)
        {
            return CommittedBatch.Empty(afterPosition);
        }

        if (rows.Count > batchSize)
        {
            var lastCompleteId = rows[batchSize - 1].TransactionId;
            var overflowId = rows[batchSize].TransactionId;
            if (overflowId == lastCompleteId)
            {
                var cut = rows.FindIndex(row => row.TransactionId == lastCompleteId);
                rows = cut == 0 ? rows : rows[..cut];
            }
            else
            {
                rows = rows[..batchSize];
            }

            if (rows.Count > batchSize)
            {
                rows = await ReadWholeTransactionAsync(context, statements, partition, lastCompleteId, cancellationToken);
            }
        }

        return new CommittedBatch([.. rows.Select(row => new CommittedEntry(row.Entry, (long)row.TransactionId))], (long)rows[^1].TransactionId);
    }

    private async Task<List<Row>> ReadWholeTransactionAsync(TContext context, Statements statements, int partition, ulong transactionId, CancellationToken cancellationToken)
    {
        var id = transactionId.ToString(CultureInfo.InvariantCulture);
        var rows = await context.Set<EventStreamEntry>()
            .FromSqlRaw(statements.ReadTransaction, _partitionCount, partition, id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return [.. rows.Select(entry => new Row(entry, transactionId))];
    }

    private sealed record Row(EventStreamEntry Entry, ulong TransactionId);

    /// <summary>The two statements, with the table and columns named as the context's model maps them.</summary>
    private sealed record Statements(string ReadAfter, string ReadTransaction)
    {
        /// <exception cref="InvalidOperationException">The model maps no event stream table, or lacks the commit-order column.</exception>
        public static Statements For(DbContext context)
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
                           ?? throw new InvalidOperationException($"{nameof(EventStreamEntry)}.{property} is not mapped in {context.GetType().Name}; the framework's write context declares it on PostgreSQL.");
                return sql.DelimitIdentifier(name);
            }

            var from = sql.DelimitIdentifier(tableName, entity.GetSchema());
            var bucket = Column(nameof(EventStreamEntry.BucketId));
            var sequence = Column(nameof(EventStreamEntry.SequenceNumber));
            var transaction = Column(CommitOrderSchema.TransactionIdColumn);

            return new Statements(
                $$"""
                SELECT * FROM {{from}}
                WHERE {{bucket}} % {0} = {1}
                  AND {{transaction}} > CAST({2} AS xid8)
                  AND {{transaction}} < pg_snapshot_xmin(pg_current_snapshot())
                ORDER BY {{transaction}}, {{sequence}}
                LIMIT {3}
                """,
                $$"""
                SELECT * FROM {{from}}
                WHERE {{bucket}} % {0} = {1}
                  AND {{transaction}} = CAST({2} AS xid8)
                ORDER BY {{sequence}}
                """);
        }
    }
}
