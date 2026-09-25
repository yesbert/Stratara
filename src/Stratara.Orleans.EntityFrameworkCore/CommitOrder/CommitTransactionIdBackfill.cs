using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;

namespace Stratara.Orleans.EntityFrameworkCore.CommitOrder;

/// <summary>
/// Stamps the commit record on the entries a PostgreSQL store held before the native reader's transaction-id
/// column existed, so that the column can be added to a populated table without rewriting it. The entries are
/// stamped in batches, each batch under a transaction of its own: every batch receives one ascending transaction
/// id, so the reader returns the history in batch-sized groups instead of the whole history under the migration's
/// single id. The reader orders a stream's entries within one transaction by version, but a save does not number
/// its entries in version order, so a batch cut by sequence number alone could leave a stream's later version in an
/// earlier batch than its first. A batch is therefore extended until no stream in it has an unstamped entry of a
/// lower version beyond it, and may hold more entries than the batch size.
/// </summary>
/// <remarks>
/// The documented migration adds the column nullable and without a default, runs this once, then sets the default
/// and the constraint. Run it while nothing appends: an entry appended meanwhile receives no id before the default
/// is set and a later id than newer entries after it, which inverts the order inside its stream. Running it again on
/// a stamped store changes nothing.
/// </remarks>
public static class CommitTransactionIdBackfill
{
    /// <summary>The batch size where none is given.</summary>
    public const int DefaultBatchSize = 1_000;

    /// <summary>Stamps every entry without a commit record, oldest first, and returns how many it stamped.</summary>
    /// <param name="context">A context derived from the framework's write context on PostgreSQL.</param>
    /// <param name="batchSize">How many entries one transaction stamps; the reader returns the history in groups of this size.</param>
    /// <param name="cancellationToken">Cancels between batches.</param>
    /// <returns>How many entries were stamped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="batchSize"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">The context's model maps no event stream table or lacks the commit-order column.</exception>
    public static async Task<int> RunAsync(DbContext context, int batchSize = DefaultBatchSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var statements = Statement.For(context, batchSize);
        var stamped = 0;
        var from = 0L;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            // Where the batch ends is read first, so the update that follows starts where the batch before it
            // ended instead of walking every entry it has already stamped.
            var to = await BoundAsync(context, statements.Bound, from, cancellationToken);
            if (to is { } initial)
            {
                to = await ExtendAsync(context, statements.Straggler, from, initial, cancellationToken);
            }

            var affected = to is null
                ? 0
                : await context.Database.ExecuteSqlRawAsync(statements.Stamp, [Parameter("from", from), Parameter("to", to.Value)], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (to is not { } bound)
            {
                return stamped;
            }

            stamped += affected;
            from = bound;
        }
    }

    private static NpgsqlParameter Parameter(string name, long value) =>
        new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Bigint) { Value = value };

    /// <summary>The highest sequence number of the next batch, or <see langword="null"/> where nothing is left.</summary>
    private static Task<long?> BoundAsync(DbContext context, string sql, long from, CancellationToken cancellationToken) =>
        ScalarAsync(context, sql, [Parameter("from", from)], cancellationToken);

    /// <summary>
    /// Moves the end of the batch to the last unstamped entry beyond it whose stream appears in the batch at a higher
    /// version, until there is none — a widened batch can bring in a stream of its own.
    /// </summary>
    private static async Task<long> ExtendAsync(DbContext context, string sql, long from, long to, CancellationToken cancellationToken)
    {
        while (await ScalarAsync(context, sql, [Parameter("from", from), Parameter("to", to)], cancellationToken) is { } straggler)
        {
            to = straggler;
        }

        return to;
    }

    private static async Task<long?> ScalarAsync(DbContext context, string sql, NpgsqlParameter[] parameters, CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>The statements, with the table and columns named as the context's model maps them.</summary>
    private static class Statement
    {
        /// <exception cref="InvalidOperationException">The model maps no event stream table, or lacks the commit-order column.</exception>
        public static (string Bound, string Straggler, string Stamp) For(DbContext context, int batchSize)
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
            var sequence = Column(nameof(EventStreamEntry.SequenceNumber));
            var transaction = Column(CommitOrderSchema.TransactionIdColumn);
            var bucket = Column(nameof(EventStreamEntry.BucketId));
            var stream = Column(nameof(EventStreamEntry.StreamId));
            var version = Column(nameof(EventStreamEntry.Version));

            var bound = $$"""
                SELECT max({{sequence}}) FROM (
                    SELECT {{sequence}} FROM {{from}}
                    WHERE {{transaction}} IS NULL AND {{sequence}} > @from
                    ORDER BY {{sequence}}
                    LIMIT {{batchSize}}
                ) AS batch
                """;

            var stamp = $$"""
                UPDATE {{from}} SET {{transaction}} = pg_current_xact_id()
                WHERE {{transaction}} IS NULL AND {{sequence}} > @from AND {{sequence}} <= @to
                """;

            var straggler = $$"""
                SELECT max(later.{{sequence}}) FROM {{from}} AS later
                JOIN (
                    SELECT {{bucket}}, {{stream}}, max({{version}}) AS highest FROM {{from}}
                    WHERE {{transaction}} IS NULL AND {{sequence}} > @from AND {{sequence}} <= @to
                    GROUP BY {{bucket}}, {{stream}}
                ) AS batch ON later.{{bucket}} = batch.{{bucket}} AND later.{{stream}} = batch.{{stream}}
                WHERE later.{{transaction}} IS NULL AND later.{{sequence}} > @to AND later.{{version}} < batch.highest
                """;

            return (bound, straggler, stamp);
        }
    }
}
