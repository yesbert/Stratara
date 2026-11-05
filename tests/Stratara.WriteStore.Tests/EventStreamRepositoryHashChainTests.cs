using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing;
using Stratara.Shared.Partitioning;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Tests;

/// <summary>
/// The reads and the write the hash-chain worker goes through: it reads the unchained entries untracked, chains them, and
/// hands them back to be stored.
/// </summary>
public class EventStreamRepositoryHashChainTests
{
    [Fact]
    public async Task Storing_the_chain_of_untracked_entries_keeps_their_commit_order_position()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await CreateStoreAsync();
        context.Set<EventStreamEntry>().AddRange(Entry(1, Settled), Entry(2, Settled));
        foreach (var entry in context.ChangeTracker.Entries<EventStreamEntry>())
        {
            entry.Property(CommitOrderSchema.PartitionPositionColumn).CurrentValue = entry.Entity.SequenceNumber * 10;
        }

        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();
        var repository = new EventStreamRepository(context);

        var unchained = await repository.GetUnhashedEventsAsync(10, DateTimeOffset.UtcNow, ct);
        foreach (var entry in unchained)
        {
            entry.PreviousHash = [0];
            entry.Hash = [(byte)entry.SequenceNumber];
        }

        repository.UpdateRange(unchained);
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        var stored = await context.Set<EventStreamEntry>()
            .OrderBy(e => e.SequenceNumber)
            .Select(e => new { e.SequenceNumber, e.Hash, e.PreviousHash, Position = EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn),
                Transaction = EF.Property<ulong>(e, CommitOrderSchema.TransactionIdColumn) })
            .ToListAsync(ct);
        Assert.Equal([10L, 20L], stored.Select(e => e.Position));
        Assert.All(stored, e => Assert.Equal(InsertingTransaction, e.Transaction));
        Assert.Equal([[1], [2]], stored.Select(e => e.Hash));
        Assert.All(stored, e => Assert.Equal([0], e.PreviousHash));
    }

    [Fact]
    public async Task The_unchained_batch_ends_before_the_first_entry_that_is_not_yet_settled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await CreateStoreAsync();
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-5);
        context.Set<EventStreamEntry>().AddRange(
            Entry(1, Settled),
            Entry(2, Settled),
            Entry(3, cutoff.AddSeconds(3)),
            Entry(4, Settled),
            Entry(5, Settled));
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        var batch = await new EventStreamRepository(context).GetUnhashedEventsAsync(10, cutoff, ct);

        Assert.Equal([1L, 2L], batch.Select(e => e.SequenceNumber));
    }

    [Fact]
    public async Task The_unchained_batch_is_empty_while_its_first_entry_is_not_yet_settled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await CreateStoreAsync();
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-5);
        context.Set<EventStreamEntry>().AddRange(Entry(1, cutoff.AddSeconds(3)), Entry(2, Settled));
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        Assert.Empty(await new EventStreamRepository(context).GetUnhashedEventsAsync(10, cutoff, ct));
    }

    [Fact]
    public async Task The_unchained_batch_runs_to_its_size_when_every_unchained_entry_is_settled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await CreateStoreAsync();
        context.Set<EventStreamEntry>().AddRange(Entry(1, Settled, hashed: true), Entry(2, Settled), Entry(3, Settled), Entry(4, Settled));
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        var batch = await new EventStreamRepository(context).GetUnhashedEventsAsync(2, DateTimeOffset.UtcNow.AddSeconds(-5), ct);

        Assert.Equal([2L, 3L], batch.Select(e => e.SequenceNumber));
    }

    /// <summary>What the database fills the transaction-id column with on insert, as PostgreSQL fills it with the inserting transaction.</summary>
    private const ulong InsertingTransaction = 42;

    private static readonly DateTimeOffset Settled = DateTimeOffset.UtcNow.AddMinutes(-10);

    private static async Task<HashChainWriteDbContext> CreateStoreAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<HashChainWriteDbContext>().UseSqlite(connection).Options;
        var context = new HashChainWriteDbContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return context;
    }

    private static EventStreamEntry Entry(long sequenceNumber, DateTimeOffset timestamp, bool hashed = false)
    {
        var streamId = Guid.NewGuid();
        return new EventStreamEntry
        {
            SequenceNumber = sequenceNumber,
            StreamId = streamId,
            Version = 1,
            EventTypeName = "TestEvent",
            AggregateTypeName = "TestAggregate",
            DataJson = "{}",
            Timestamp = timestamp,
            CorrelationId = Guid.NewGuid().ToString("N"),
            CausationId = Guid.NewGuid().ToString("N"),
            BucketId = BucketCalculator.GetBucketId(streamId),
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            ActorTenantId = Guid.NewGuid(),
            ActorUserId = Guid.NewGuid(),
            Hash = hashed ? [9] : null,
        };
    }

    /// <summary>
    /// SQLite compares no date-time offset, so the timestamp the cut-off reads is stored as a number. The transaction-id
    /// column is declared as PostgreSQL declares it, filled by the database on insert, with a constant in place of the
    /// transaction id SQLite does not have.
    /// </summary>
    private sealed class HashChainWriteDbContext(DbContextOptions<HashChainWriteDbContext> options)
        : WriteDbContext<HashChainWriteDbContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<EventStreamEntry>().Property(e => e.Timestamp).HasConversion(new DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<EventStreamEntry>().Property<ulong>(CommitOrderSchema.TransactionIdColumn)
                .HasDefaultValueSql(InsertingTransaction.ToString(CultureInfo.InvariantCulture))
                .ValueGeneratedOnAdd();
        }
    }
}
