using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.CommitOrder;

/// <summary>
/// The native reader takes the table and column names from the context's model, so a consumer whose
/// model does not follow the snake-case convention reads the store as a conventional one does.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class NativeReaderModelNamesTests(PostgreSqlFixture postgres)
{
    private const string Database = "poc_commit_order_pascal";

    [Fact]
    public async Task The_native_reader_reads_a_store_whose_names_are_not_snake_case()
    {
        await using var store = await PocStore<PascalCaseWriteDbContext>.CreateAsync(
            postgres.ConnectionStringFor(Database),
            options => options.MaintainPartitionCounter = false);
        var reader = new PostgresTransactionIdReader<PascalCaseWriteDbContext>(store.ContextFactory, Options.Create(store.Options));

        var bucketId = Random.Shared.Next(0, 4096);
        var partition = PartitionMap.PartitionOf(bucketId, store.Options.PartitionCount);
        var before = await DrainAsync(reader, partition, 0, []);
        var written = new[]
        {
            PocStore<PascalCaseWriteDbContext>.NewEntry(Guid.NewGuid(), 1, bucketId, Guid.NewGuid()),
            PocStore<PascalCaseWriteDbContext>.NewEntry(Guid.NewGuid(), 1, bucketId, Guid.NewGuid()),
        };
        await using (var context = await store.CreateContextAsync())
        {
            context.Set<EventStreamEntry>().AddRange(written);
            await context.SaveChangesAsync();
        }

        var seen = new HashSet<Guid>();
        await DrainAsync(reader, partition, before, seen);

        Assert.Subset(seen, written.Select(entry => entry.Id).ToHashSet());
    }

    private static async Task<long> DrainAsync(PostgresTransactionIdReader<PascalCaseWriteDbContext> reader, int partition, long position, HashSet<Guid> seen)
    {
        while (true)
        {
            var batch = await reader.ReadAfterAsync(partition, position, 100);
            if (batch.Entries.Count == 0)
            {
                return batch.Position;
            }

            foreach (var entry in batch.Entries)
            {
                seen.Add(entry.Entry.Id);
            }

            position = batch.Position;
        }
    }
}

/// <summary>The shipped write model with the event stream renamed away from the snake-case convention.</summary>
public sealed class PascalCaseWriteDbContext(DbContextOptions<PascalCaseWriteDbContext> options)
    : WriteDbContext<PascalCaseWriteDbContext>(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        CommitOrderModel.Apply(modelBuilder, postgres: true);

        var entry = modelBuilder.Entity<EventStreamEntry>();
        entry.ToTable("EventStreamEntries");
        entry.Property(e => e.BucketId).HasColumnName("BucketId");
        entry.Property(e => e.SequenceNumber).HasColumnName("SequenceNumber");
        entry.Property<ulong>(CommitOrderModel.TransactionIdColumn).HasColumnName("CommitTransactionId");
    }
}
