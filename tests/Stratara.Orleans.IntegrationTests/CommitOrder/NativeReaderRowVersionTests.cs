using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.Conventions;
using Stratara.EventSourcing.EntityFrameworkCore.Extensions;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.CommitOrder;

/// <summary>
/// The native reader names the columns the model maps, so a write context that applies the row-version
/// convention — which maps the entry's row version onto PostgreSQL's <c>xmin</c> system column, a column
/// a wildcard does not return — is read like any other.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class NativeReaderRowVersionTests(PostgreSqlFixture postgres)
{
    private const string Database = "poc_commit_order_row_version";

    [Fact]
    public async Task The_native_reader_reads_a_store_whose_row_version_is_the_system_column()
    {
        await using var store = await PocStore<RowVersionWriteDbContext>.CreateAsync(
            postgres.ConnectionStringFor(Database),
            maintainCounter: false);
        var reader = new PostgresTransactionIdReader<RowVersionWriteDbContext>(store.ContextFactory, Options.Create(store.Options));

        var bucketId = Random.Shared.Next(0, 4096);
        var partition = PartitionMap.PartitionOf(bucketId, store.Options.PartitionCount);
        var before = await DrainAsync(reader, partition, 0, []);
        var written = new[]
        {
            PocStore<RowVersionWriteDbContext>.NewEntry(Guid.NewGuid(), 1, bucketId, Guid.NewGuid()),
            PocStore<RowVersionWriteDbContext>.NewEntry(Guid.NewGuid(), 1, bucketId, Guid.NewGuid()),
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

    [Fact]
    public async Task The_native_reader_returns_a_whole_transaction_of_such_a_store()
    {
        await using var store = await PocStore<RowVersionWriteDbContext>.CreateAsync(
            postgres.ConnectionStringFor(Database + "_whole"),
            maintainCounter: false);
        var reader = new PostgresTransactionIdReader<RowVersionWriteDbContext>(store.ContextFactory, Options.Create(store.Options));

        var bucketId = Random.Shared.Next(0, 4096);
        var partition = PartitionMap.PartitionOf(bucketId, store.Options.PartitionCount);
        var before = await DrainAsync(reader, partition, 0, []);
        var streamId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var written = new[]
        {
            PocStore<RowVersionWriteDbContext>.NewEntry(streamId, 1, bucketId, tenantId),
            PocStore<RowVersionWriteDbContext>.NewEntry(streamId, 2, bucketId, tenantId),
            PocStore<RowVersionWriteDbContext>.NewEntry(streamId, 3, bucketId, tenantId),
        };
        await using (var context = await store.CreateContextAsync())
        {
            context.Set<EventStreamEntry>().AddRange(written);
            await context.SaveChangesAsync();
        }

        // A batch smaller than the transaction takes the whole-transaction path, which reads with the
        // second statement.
        var batch = await reader.ReadAfterAsync(partition, before, 2);

        Assert.Equal(written.Length, batch.Entries.Count);
    }

    private static async Task<long> DrainAsync(PostgresTransactionIdReader<RowVersionWriteDbContext> reader, int partition, long position, HashSet<Guid> seen)
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

/// <summary>
/// The shipped write model with the framework's row-version convention applied, the mode the API
/// reference names for PostgreSQL. Npgsql maps the row version onto the <c>xmin</c> system column.
/// </summary>
public sealed class RowVersionWriteDbContext(DbContextOptions<RowVersionWriteDbContext> options)
    : WriteDbContext<RowVersionWriteDbContext>(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyRowVersionConvention(RowVersionMode.Uint);
    }
}
