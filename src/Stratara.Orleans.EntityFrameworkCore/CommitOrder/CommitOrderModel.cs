using Stratara.Abstractions.CommitOrder;
using Stratara.Orleans.CommitOrder;
using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Orleans.EntityFrameworkCore.CommitOrder;

/// <summary>
/// The proof of concept's additions to the shipped write model: the columns the commit-order readers
/// need, kept as shadow properties so the shipped <see cref="EventStreamEntry"/> and the write path
/// that fills it stay untouched, and the counter table the portable reader orders by.
/// </summary>
/// <remarks>
/// Shipping any of this changes the schema a consumer migrates to, which is a follow-up change with
/// its own migration note. Until then a context opts in by calling <see cref="Apply"/> from its
/// <c>OnModelCreating</c> after the base model.
/// </remarks>
public static class CommitOrderModel
{
    /// <summary>The shadow column holding the PostgreSQL transaction id that inserted the entry.</summary>
    public const string TransactionIdColumn = "commit_transaction_id";

    /// <summary>The shadow column holding the entry's position in its partition's counter order.</summary>
    public const string PartitionPositionColumn = "partition_position";

    /// <summary>The counter table, one row per partition.</summary>
    public const string PartitionPositionTable = "partition_position";

    /// <summary>
    /// Adds the shadow columns to <see cref="EventStreamEntry"/> and the counter entity to the model.
    /// </summary>
    /// <param name="modelBuilder">The context's model builder, after the base model is applied.</param>
    /// <param name="postgres">
    /// Whether the store is PostgreSQL. Only then does the transaction-id column exist, filled by the
    /// database on insert; every other provider gets the counter column alone.
    /// </param>
    public static void Apply(ModelBuilder modelBuilder, bool postgres)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var entry = modelBuilder.Entity<EventStreamEntry>();
        entry.Property<long?>(PartitionPositionColumn);
        entry.HasIndex(PartitionPositionColumn);

        if (postgres)
        {
            entry.Property<ulong>(TransactionIdColumn)
                .HasColumnType("xid8")
                .HasDefaultValueSql("pg_current_xact_id()")
                .ValueGeneratedOnAdd();
            entry.HasIndex(TransactionIdColumn);
        }

        modelBuilder.Entity<PartitionPosition>(counter =>
        {
            counter.ToTable(PartitionPositionTable);
            counter.HasKey(c => c.Partition);
            counter.Property(c => c.Partition).ValueGeneratedNever();
        });
    }
}

/// <summary>
/// One row per partition: the last position handed out to an entry in that partition. Incremented
/// inside the transaction that appends the entry, so that commit order and position order agree.
/// </summary>
public sealed class PartitionPosition
{
    /// <summary>The partition this counter belongs to.</summary>
    public int Partition { get; set; }

    /// <summary>The last position handed out.</summary>
    public long Position { get; set; }
}
