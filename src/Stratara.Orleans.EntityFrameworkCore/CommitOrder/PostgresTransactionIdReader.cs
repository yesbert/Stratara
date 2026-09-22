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
/// snake-case convention is read the same way. Each statement names the columns the model maps
/// rather than selecting them all, because a wildcard returns no system column: a context that maps
/// a property onto one — which the framework's row-version convention does on PostgreSQL — would
/// otherwise be read with a statement the database rejects.
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
    public string Name => $"postgres-transaction-id/{_partitionCount}";

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
        // The projection wraps the statement in a subquery, whose ORDER BY the outer query need not keep;
        // the cut below depends on the order, so it is restored here.
        var rows = projected
            .Select(row => new Row(row.Entry, row.TransactionId))
            .GroupBy(row => row.TransactionId)
            .OrderBy(group => group.Key)
            .SelectMany(InStreamOrder)
            .ToList();

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

        return new CommittedBatch([.. rows.Select(row => new CommittedEntry(row.Entry, (long)row.TransactionId))], (long)rows[^1].TransactionId)
        {
            HasMore = projected.Count > batchSize,
        };
    }

    /// <inheritdoc/>
    public async Task<long> HeadAsync(int partition, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var statements = _statements ??= Statements.For(context);
        var head = await context.Database
            .SqlQueryRaw<string?>(statements.Head, _partitionCount, partition)
            .SingleAsync(cancellationToken);
        return head is null ? 0 : (long)ulong.Parse(head, CultureInfo.InvariantCulture);
    }

    private async Task<List<Row>> ReadWholeTransactionAsync(TContext context, Statements statements, int partition, ulong transactionId, CancellationToken cancellationToken)
    {
        var id = transactionId.ToString(CultureInfo.InvariantCulture);
        var rows = await context.Set<EventStreamEntry>()
            .FromSqlRaw(statements.ReadTransaction, _partitionCount, partition, id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return [.. InStreamOrder(rows.Select(entry => new Row(entry, transactionId)))];
    }

    /// <summary>
    /// The entries of one commit in the order a consumer reads them: each stream's entries in version
    /// order, the streams themselves in the order the store numbered their first entry. The order the
    /// rows were inserted in is the database's to choose and is not the order the entries were
    /// appended in, so a stream's creating fact can carry the higher sequence number.
    /// </summary>
    private static IEnumerable<Row> InStreamOrder(IEnumerable<Row> rows) =>
        rows.GroupBy(row => row.Entry.StreamId)
            .OrderBy(group => group.Min(row => row.Entry.SequenceNumber))
            .SelectMany(group => group.OrderBy(row => row.Entry.Version));

    private sealed record Row(EventStreamEntry Entry, ulong TransactionId);

    /// <summary>The two statements, with the table and columns named as the context's model maps them.</summary>
    private sealed record Statements(string ReadAfter, string ReadTransaction, string Head)
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
            var columns = string.Join(
                ", ",
                ColumnsOf(entity, table)
                    .Distinct(StringComparer.Ordinal)
                    .Select(sql.DelimitIdentifier));

            return new Statements(
                $$"""
                SELECT {{columns}} FROM {{from}}
                WHERE {{bucket}} % {0} = {1}
                  AND {{transaction}} > CAST({2} AS xid8)
                  AND {{transaction}} < pg_snapshot_xmin(pg_current_snapshot())
                ORDER BY {{transaction}}, {{sequence}}
                LIMIT {3}
                """,
                $$"""
                SELECT {{columns}} FROM {{from}}
                WHERE {{bucket}} % {0} = {1}
                  AND {{transaction}} = CAST({2} AS xid8)
                ORDER BY {{sequence}}
                """,
                $$"""
                SELECT CAST(MAX({{transaction}}) AS text) AS "Value" FROM {{from}}
                WHERE {{bucket}} % {0} = {1}
                  AND {{transaction}} < pg_snapshot_xmin(pg_current_snapshot())
                """);
        }

        /// <summary>
        /// Every column the type maps in that table, complex properties flattened, so that a context
        /// mapping more than the framework's own scalars is still read through the columns it declares.
        /// </summary>
        private static IEnumerable<string> ColumnsOf(ITypeBase type, StoreObjectIdentifier table)
        {
            foreach (var property in type.GetProperties())
            {
                if (property.GetColumnName(table) is { } column)
                {
                    yield return column;
                }
            }

            foreach (var complex in type.GetComplexProperties())
            {
                foreach (var column in ColumnsOf(complex.ComplexType, table))
                {
                    yield return column;
                }
            }
        }
    }
}
