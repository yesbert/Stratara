using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

namespace Stratara.Orleans.CommitOrder;

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
/// reader delays rather than skips under one — the benchmarks record the delay.
/// </para>
/// </remarks>
/// <typeparam name="TContext">The write context, with <see cref="CommitOrderModel"/> applied for PostgreSQL.</typeparam>
public sealed class PostgresTransactionIdReader<TContext>(IDbContextFactory<TContext> contextFactory, IOptions<CommitOrderOptions> options)
    : ICommittedPositionReader
    where TContext : DbContext, IWriteDbContext
{
    private readonly int _partitionCount = options.Value.PartitionCount;

    /// <inheritdoc/>
    public async Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var after = ((ulong)afterPosition).ToString(CultureInfo.InvariantCulture);

        var projected = await context.Set<EventStreamEntry>()
            .FromSql($"""
                SELECT * FROM event_stream_entry
                WHERE bucket_id % {_partitionCount} = {partition}
                  AND commit_transaction_id > CAST({after} AS xid8)
                  AND commit_transaction_id < pg_snapshot_xmin(pg_current_snapshot())
                ORDER BY commit_transaction_id, sequence_number
                LIMIT {batchSize + 1}
                """)
            .AsNoTracking()
            .Select(e => new { Entry = e, TransactionId = EF.Property<ulong>(e, CommitOrderModel.TransactionIdColumn) })
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
                rows = await ReadWholeTransactionAsync(context, partition, lastCompleteId, cancellationToken);
            }
        }

        return new CommittedBatch([.. rows.Select(row => new CommittedEntry(row.Entry, (long)row.TransactionId))], (long)rows[^1].TransactionId);
    }

    private async Task<List<Row>> ReadWholeTransactionAsync(TContext context, int partition, ulong transactionId, CancellationToken cancellationToken)
    {
        var id = transactionId.ToString(CultureInfo.InvariantCulture);
        var rows = await context.Set<EventStreamEntry>()
            .FromSql($"""
                SELECT * FROM event_stream_entry
                WHERE bucket_id % {_partitionCount} = {partition}
                  AND commit_transaction_id = CAST({id} AS xid8)
                ORDER BY sequence_number
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return [.. rows.Select(entry => new Row(entry, transactionId))];
    }

    private sealed record Row(EventStreamEntry Entry, ulong TransactionId);
}
