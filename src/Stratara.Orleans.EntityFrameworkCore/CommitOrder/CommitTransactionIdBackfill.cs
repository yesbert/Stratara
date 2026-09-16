using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;

namespace Stratara.Orleans.EntityFrameworkCore.CommitOrder;

/// <summary>
/// Stamps the commit record on the entries a PostgreSQL store held before the native reader's transaction-id
/// column existed, so that the column can be added to a populated table without rewriting it. The entries are
/// stamped in the order they were appended, in batches, each batch under a transaction of its own: every batch
/// receives one ascending transaction id, so the reader returns the history in batch-sized groups and in append
/// order instead of the whole history under the migration's single id.
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

        var statement = Statement.For(context, batchSize);
        var stamped = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var affected = await context.Database.ExecuteSqlRawAsync(statement, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (affected == 0)
            {
                return stamped;
            }

            stamped += affected;
        }
    }

    /// <summary>The update, with the table and columns named as the context's model maps them.</summary>
    private static class Statement
    {
        /// <exception cref="InvalidOperationException">The model maps no event stream table, or lacks the commit-order column.</exception>
        public static string For(DbContext context, int batchSize)
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

            return $$"""
                UPDATE {{from}} SET {{transaction}} = pg_current_xact_id()
                WHERE {{sequence}} IN (
                    SELECT {{sequence}} FROM {{from}}
                    WHERE {{transaction}} IS NULL
                    ORDER BY {{sequence}}
                    LIMIT {{batchSize}}
                )
                """;
        }
    }
}
