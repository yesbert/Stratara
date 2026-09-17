using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.CommitOrder;

/// <summary>
/// The portable reader fails loudly where it would otherwise lose facts: a read stops at an entry appended without
/// a position — however many unpositioned entries other partitions hold — and resumes from its checkpoint once the
/// entry is positioned after it, a host refuses to start with a partition count lower than the store's counters, and
/// a save whose positioning failed leaves no transaction behind for the next save on the same context (scenarios <em>A
/// late entry is positioned behind a checkpoint</em>, <em>Unpositioned entries pile up in other partitions</em>).
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class PortableReaderFailsLoudlyTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task A_read_stops_at_an_entry_appended_without_a_position_until_it_is_positioned()
    {
        var connectionString = postgres.ConnectionStringFor("poc_portable_unpositioned");
        await using var counted = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, maintainCounter: true);
        await using var uncounted = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, maintainCounter: false);
        await ResetAsync(counted);
        var reader = new PortableCounterReader<PocCommitOrderWriteDbContext>(counted.ContextFactory, Options.Create(counted.Options));
        var partitions = counted.Options.PartitionCount;
        const int bucket = 5;
        var partition = PartitionMap.PartitionOf(bucket, partitions);
        var otherPartition = (partition + 1) % partitions;

        var positioned = await AppendAsync(counted, bucket);
        var unpositioned = await AppendAsync(uncounted, bucket);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAfterAsync(partition, 0, 100, TestContext.Current.CancellationToken));
        Assert.Contains(nameof(PartitionCounterInterceptor), refused.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(PartitionCounterBackfill), refused.Message, StringComparison.Ordinal);
        Assert.Empty((await reader.ReadAfterAsync(otherPartition, 0, 100, TestContext.Current.CancellationToken)).Entries);

        await using (var context = await counted.CreateContextAsync())
        {
            Assert.Equal(1, await PartitionCounterBackfill.RunAsync(context, counted.Options, TestContext.Current.CancellationToken));
        }

        var batch = await reader.ReadAfterAsync(partition, 0, 100, TestContext.Current.CancellationToken);
        Assert.Equal(
            new[] { positioned, unpositioned }.Order(),
            batch.Entries.Select(entry => entry.Entry.Id).Order());
    }

    [Fact]
    public async Task A_late_entry_is_positioned_after_a_checkpoint_and_nothing_positioned_before_moves()
    {
        var connectionString = postgres.ConnectionStringFor("poc_portable_late_entry");
        await using var counted = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, maintainCounter: true);
        await using var uncounted = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, maintainCounter: false);
        await ResetAsync(counted);
        var reader = new PortableCounterReader<PocCommitOrderWriteDbContext>(counted.ContextFactory, Options.Create(counted.Options));
        const int bucket = 5;
        var partition = PartitionMap.PartitionOf(bucket, counted.Options.PartitionCount);

        var positioned = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            positioned.Add(await AppendAsync(counted, bucket));
        }

        var beforeBackfill = await reader.ReadAfterAsync(partition, 0, 100, TestContext.Current.CancellationToken);
        var checkpoint = beforeBackfill.Position;
        var late = await AppendAsync(uncounted, bucket);
        await using (var context = await counted.CreateContextAsync())
        {
            Assert.Equal(1, await PartitionCounterBackfill.RunAsync(context, counted.Options, TestContext.Current.CancellationToken));
        }

        var resumed = await reader.ReadAfterAsync(partition, checkpoint, 100, TestContext.Current.CancellationToken);
        Assert.Equal([late], resumed.Entries.Select(entry => entry.Entry.Id));
        Assert.Equal(checkpoint + 1, resumed.Entries[0].Position);
        var fromStart = await reader.ReadAfterAsync(partition, 0, 100, TestContext.Current.CancellationToken);
        Assert.Equal(
            beforeBackfill.Entries.Select(entry => (entry.Entry.Id, entry.Position)),
            fromStart.Entries.Take(positioned.Count).Select(entry => (entry.Entry.Id, entry.Position)));
    }

    [Fact]
    public async Task A_read_stops_at_its_own_unpositioned_entry_however_many_other_partitions_hold()
    {
        var connectionString = postgres.ConnectionStringFor("poc_portable_probe");
        await using var uncounted = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, maintainCounter: false);
        await ResetAsync(uncounted);
        var reader = new PortableCounterReader<PocCommitOrderWriteDbContext>(uncounted.ContextFactory, Options.Create(uncounted.Options));
        const int bucket = 5;
        const int readSize = 100;
        var partitions = uncounted.Options.PartitionCount;
        var partition = PartitionMap.PartitionOf(bucket, partitions);
        var foreignBucket = Enumerable.Range(0, 4096).First(b => PartitionMap.PartitionOf(b, partitions) != partition);

        await AppendManyAsync(uncounted, foreignBucket, readSize + 1);
        var own = await AppendAsync(uncounted, bucket);
        await AppendManyAsync(uncounted, foreignBucket, readSize + 1);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAfterAsync(partition, 0, readSize, TestContext.Current.CancellationToken));
        Assert.Contains($"of partition {partition} ", refused.Message, StringComparison.Ordinal);
        await using var context = await uncounted.CreateContextAsync();
        var sequenceNumber = await context.Set<EventStreamEntry>().Where(e => e.Id == own).Select(e => e.SequenceNumber).SingleAsync(TestContext.Current.CancellationToken);
        Assert.Contains($"Entry {sequenceNumber} ", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_with_a_partition_count_lower_than_the_stores_counters_refuses_to_start()
    {
        var connectionString = postgres.ConnectionStringFor("poc_portable_partition_count");
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, options => options.PartitionCount = 8);
        await ResetAsync(store);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => StartCheck(store, partitionCount: 4).StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("partition 7", refused.Message, StringComparison.Ordinal);
        Assert.Contains("partition count of 4", refused.Message, StringComparison.Ordinal);

        await StartCheck(store, partitionCount: 8).StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_save_whose_positioning_failed_leaves_no_transaction_for_the_next_save_on_the_context()
    {
        var connectionString = postgres.ConnectionStringFor("poc_portable_interceptor_failure");
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, maintainCounter: true);
        await ResetAsync(store);
        const int bucket = 3;
        var partition = PartitionMap.PartitionOf(bucket, store.Options.PartitionCount);
        await using (var context = await store.CreateContextAsync())
        {
            await context.Set<PartitionPosition>().Where(c => c.Partition == partition).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        var entry = PocStore<PocCommitOrderWriteDbContext>.NewEntry(Guid.NewGuid(), 1, bucket, Guid.NewGuid());
        await using (var context = await store.CreateContextAsync())
        {
            context.Set<EventStreamEntry>().Add(entry);
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));

            await using (var seeding = await store.CreateContextAsync())
            {
                seeding.Set<PartitionPosition>().Add(new PartitionPosition { Partition = partition, Position = 0 });
                await seeding.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = await store.CreateContextAsync())
        {
            var saved = await context.Set<EventStreamEntry>().AsNoTracking()
                .Where(e => e.Id == entry.Id)
                .Select(e => EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn))
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal([1L], saved);
        }
    }

    private static PortableCounterStartupCheck<PocCommitOrderWriteDbContext> StartCheck(PocStore<PocCommitOrderWriteDbContext> store, int partitionCount) =>
        new(store.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CommitOrderOptions { PartitionCount = partitionCount }));

    private static async Task<Guid> AppendAsync(PocStore<PocCommitOrderWriteDbContext> store, int bucket)
    {
        var entry = PocStore<PocCommitOrderWriteDbContext>.NewEntry(Guid.NewGuid(), 1, bucket, Guid.NewGuid());
        await using var context = await store.CreateContextAsync();
        context.Set<EventStreamEntry>().Add(entry);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return entry.Id;
    }

    private static async Task AppendManyAsync(PocStore<PocCommitOrderWriteDbContext> store, int bucket, int count)
    {
        await using var context = await store.CreateContextAsync();
        for (var i = 0; i < count; i++)
        {
            context.Set<EventStreamEntry>().Add(PocStore<PocCommitOrderWriteDbContext>.NewEntry(Guid.NewGuid(), 1, bucket, Guid.NewGuid()));
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task ResetAsync(PocStore<PocCommitOrderWriteDbContext> store)
    {
        await using var context = await store.CreateContextAsync();
        await context.Set<EventStreamEntry>().ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await context.Set<PartitionPosition>().ExecuteUpdateAsync(set => set.SetProperty(c => c.Position, 0), TestContext.Current.CancellationToken);
    }
}
