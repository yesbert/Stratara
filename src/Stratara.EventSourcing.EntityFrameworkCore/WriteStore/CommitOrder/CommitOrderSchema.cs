using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;

/// <summary>
/// The names of what the write model declares for reading the event stream in commit order: a
/// per-partition position on every entry with the counter table it is handed out from, and on
/// PostgreSQL the id of the transaction that inserted the entry. A store that does not read in commit
/// order carries the columns and never fills them.
/// </summary>
public static class CommitOrderSchema
{
    /// <summary>The property and column holding the PostgreSQL transaction id that inserted the entry. PostgreSQL only.</summary>
    public const string TransactionIdColumn = "commit_transaction_id";

    /// <summary>The property and column holding the entry's position in its partition's counter order.</summary>
    public const string PartitionPositionColumn = "partition_position";

    /// <summary>The counter table, one row per partition.</summary>
    public const string PartitionPositionTable = "partition_position";

    /// <summary>
    /// Adds the transaction-id column to <see cref="EventStreamEntry"/>, filled by the database on
    /// insert, with the index the native reader orders by.
    /// </summary>
    /// <param name="modelBuilder">The write context's model builder, after the base configurations.</param>
    internal static void ApplyPostgresTransactionId(ModelBuilder modelBuilder)
    {
        var entry = modelBuilder.Entity<EventStreamEntry>();
        entry.Property<ulong>(TransactionIdColumn)
            .HasColumnType("xid8")
            .HasDefaultValueSql("pg_current_xact_id()")
            .ValueGeneratedOnAdd();
        entry.HasIndex(TransactionIdColumn);
    }
}
