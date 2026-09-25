using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.CommitOrder;

/// <summary>
/// Task 2.4: a store that holds entries from before it adopted the partition counter. A host reading
/// with the portable reader refuses to start and names the backfill; the backfill positions the old
/// entries in the order they were appended, after the ones appended with the counter since, whose positions it never
/// changes; a host started afterwards reads every entry of a partition in position order. On a store nothing appended
/// to with the counter yet, the history is read in the order it was appended (scenario <em>A store with history adopts
/// the portable reader</em>).
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class PartitionCounterBackfillTests(PostgreSqlFixture postgres)
{
    private const string Database = "poc_counter_backfill";
    private const int PartitionCount = 4;

    [Fact]
    public async Task A_store_with_history_is_refused_positioned_by_the_backfill_and_then_read_in_order()
    {
        var connectionString = postgres.ConnectionStringFor(Database);
        await using var before = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, Configure, maintainCounter: false);
        await using (var context = await before.CreateContextAsync())
        {
            await context.Set<EventStreamEntry>().ExecuteDeleteAsync();
            await context.Database.ExecuteSqlRawAsync("UPDATE partition_position SET position = 0");
        }

        // Buckets fall while sequence numbers rise, so an order by bucket and an order of appending disagree in every partition.
        var history = await AppendAsync(before, count: 12, bucketOf: i => (12 - i) * 7);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => StartHostAsync(connectionString));
        Assert.Contains(nameof(PartitionCounterBackfill), refused.Message, StringComparison.Ordinal);

        await using var after = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, Configure, maintainCounter: true);
        var live = await AppendAsync(after, count: 8, bucketOf: _ => Random.Shared.Next(0, 4096));

        await using (var context = await after.CreateContextAsync())
        {
            Assert.Equal(12, await PartitionCounterBackfill.RunAsync(context, after.Options));
            Assert.Equal(0, await PartitionCounterBackfill.RunAsync(context, after.Options));
        }

        using var host = await StartHostAsync(connectionString);
        await using var scope = host.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<ICommittedPositionReader>();

        for (var partition = 0; partition < PartitionCount; partition++)
        {
            var read = await DrainAsync(reader, partition);
            var expectedHistory = history.Where(e => PartitionMap.PartitionOf(e.BucketId, PartitionCount) == partition)
                .OrderBy(e => e.SequenceNumber).Select(e => e.Id);
            var expectedLive = live.Where(e => PartitionMap.PartitionOf(e.BucketId, PartitionCount) == partition)
                .OrderBy(e => e.SequenceNumber).Select(e => e.Id);

            // The live entries were positioned when they were appended; the backfill positions history after them and moves none.
            Assert.Equal(expectedLive.Concat(expectedHistory), read.Select(e => e.Entry.Id));
            Assert.True(read.Select(e => e.Position).SequenceEqual(read.Select(e => e.Position).Order()), $"partition {partition} is not in position order");
            Assert.Equal(read.Count, read.Select(e => e.Position).Distinct().Count());
        }

        await host.StopAsync();
    }

    private static void Configure(CommitOrderOptions options) => options.PartitionCount = PartitionCount;

    [Fact]
    public async Task A_store_with_history_only_is_read_in_append_order_after_the_backfill()
    {
        var connectionString = postgres.ConnectionStringFor(Database + "_history_only");
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, Configure, maintainCounter: false);
        await using (var context = await store.CreateContextAsync())
        {
            await context.Set<EventStreamEntry>().ExecuteDeleteAsync();
            await context.Database.ExecuteSqlRawAsync("UPDATE partition_position SET position = 0");
        }

        var history = await AppendAsync(store, count: 12, bucketOf: i => (12 - i) * 7);
        await using (var context = await store.CreateContextAsync())
        {
            Assert.Equal(12, await PartitionCounterBackfill.RunAsync(context, store.Options));
        }

        var reader = new PortableCounterReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options));
        for (var partition = 0; partition < PartitionCount; partition++)
        {
            var read = await DrainAsync(reader, partition);
            var expected = history.Where(e => PartitionMap.PartitionOf(e.BucketId, PartitionCount) == partition).OrderBy(e => e.SequenceNumber).Select(e => e.Id);
            Assert.Equal(expected, read.Select(e => e.Entry.Id));
            Assert.Equal(Enumerable.Range(1, read.Count).Select(i => (long)i), read.Select(e => e.Position));
        }
    }

    [Fact]
    public async Task Interleaved_partitions_are_positioned_across_batches_in_append_order_after_their_counters()
    {
        const int perPartition = 1_100;
        var connectionString = postgres.ConnectionStringFor(Database + "_batches");
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, Configure, maintainCounter: false);
        await using (var context = await store.CreateContextAsync())
        {
            await context.Set<EventStreamEntry>().ExecuteDeleteAsync();
            for (var partition = 0; partition < PartitionCount; partition++)
            {
                var start = 100L * (partition + 1);
                await context.Set<PartitionPosition>().Where(c => c.Partition == partition)
                    .ExecuteUpdateAsync(set => set.SetProperty(c => c.Position, start));
            }

            var tenantId = Guid.NewGuid();
            context.Set<EventStreamEntry>().AddRange(Enumerable.Range(0, PartitionCount * perPartition)
                .Select(i => PocStore<PocCommitOrderWriteDbContext>.NewEntry(Guid.NewGuid(), 1, i, tenantId)));
            await context.SaveChangesAsync();
        }

        await using (var context = await store.CreateContextAsync())
        {
            Assert.Equal(PartitionCount * perPartition, await PartitionCounterBackfill.RunAsync(context, store.Options));

            var entries = await context.Set<EventStreamEntry>()
                .OrderBy(e => e.SequenceNumber)
                .Select(e => new { e.BucketId, Position = EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn) })
                .ToListAsync();
            var counters = await context.Set<PartitionPosition>().AsNoTracking().ToDictionaryAsync(c => c.Partition, c => c.Position);
            for (var partition = 0; partition < PartitionCount; partition++)
            {
                var start = 100L * (partition + 1);
                var positions = entries.Where(e => PartitionMap.PartitionOf(e.BucketId, PartitionCount) == partition).Select(e => e.Position);
                Assert.Equal(Enumerable.Range(1, perPartition).Select(i => (long?)(start + i)), positions);
                Assert.Equal(start + perPartition, counters[partition]);
            }
        }
    }

    /// <summary>
    /// Two streams whose sequence numbers run against their versions, one early in the partition and one where the
    /// first backfill batch would end between its versions: both are positioned in version order, the second after
    /// the batch was extended, and the portable reader returns both in version order.
    /// </summary>
    [Fact]
    public async Task Inverted_streams_are_positioned_in_version_order_with_and_without_a_batch_boundary_between_them()
    {
        const int bucketId = 8;
        const int fillers = 995;
        var connectionString = postgres.ConnectionStringFor(Database + "_inverted");
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, Configure, maintainCounter: false);
        await using (var context = await store.CreateContextAsync())
        {
            await context.Set<EventStreamEntry>().ExecuteDeleteAsync();
            await context.Database.ExecuteSqlRawAsync("UPDATE partition_position SET position = 0");
        }

        var early = Guid.NewGuid();
        var straddling = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        await AppendOneByOneAsync(store, bucketId, tenantId, (early, 3), (early, 2), (early, 1));
        await using (var context = await store.CreateContextAsync())
        {
            context.Set<EventStreamEntry>().AddRange(Enumerable.Range(0, fillers)
                .Select(_ => PocStore<PocCommitOrderWriteDbContext>.NewEntry(Guid.NewGuid(), 1, bucketId, tenantId)));
            await context.SaveChangesAsync();
        }

        await AppendOneByOneAsync(store, bucketId, tenantId, (straddling, 3), (straddling, 2), (straddling, 1));

        await using (var context = await store.CreateContextAsync())
        {
            Assert.Equal(fillers + 6, await PartitionCounterBackfill.RunAsync(context, store.Options));
        }

        var reader = new PortableCounterReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options));
        var read = await DrainAsync(reader, PartitionMap.PartitionOf(bucketId, PartitionCount));

        Assert.Equal(fillers + 6, read.Count);
        Assert.Equal([1L, 2L, 3L], read.Where(e => e.Entry.StreamId == early).Select(e => e.Entry.Version));
        Assert.Equal([1L, 2L, 3L], read.Where(e => e.Entry.StreamId == straddling).Select(e => e.Entry.Version));
        Assert.Equal(Enumerable.Range(1, read.Count).Select(i => (long)i), read.Select(e => e.Position));
    }

    private static async Task AppendOneByOneAsync(PocStore<PocCommitOrderWriteDbContext> store, int bucketId, Guid tenantId, params (Guid Stream, long Version)[] entries)
    {
        foreach (var (stream, version) in entries)
        {
            await using var context = await store.CreateContextAsync();
            context.Set<EventStreamEntry>().Add(PocStore<PocCommitOrderWriteDbContext>.NewEntry(stream, version, bucketId, tenantId));
            await context.SaveChangesAsync();
        }
    }

    private static async Task<List<EventStreamEntry>> AppendAsync(PocStore<PocCommitOrderWriteDbContext> store, int count, Func<int, int> bucketOf)
    {
        var tenantId = Guid.NewGuid();
        var written = new List<EventStreamEntry>();
        for (var i = 0; i < count; i++)
        {
            await using var context = await store.CreateContextAsync();
            var entry = PocStore<PocCommitOrderWriteDbContext>.NewEntry(Guid.NewGuid(), 1, bucketOf(i), tenantId);
            context.Set<EventStreamEntry>().Add(entry);
            await context.SaveChangesAsync();
            written.Add(entry);
        }

        return written;
    }

    private static async Task<IHost> StartHostAsync(string connectionString)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:defaultdb"] = connectionString });
        builder.Services
            .Configure<CommitOrderOptions>(Configure)
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddStrataraPortableCounterReader<PocCommitOrderWriteDbContext>();

        var host = builder.Build();
        try
        {
            await host.StartAsync();
        }
        catch
        {
            host.Dispose();
            throw;
        }

        return host;
    }

    private static async Task<List<CommittedEntry>> DrainAsync(ICommittedPositionReader reader, int partition)
    {
        var read = new List<CommittedEntry>();
        var position = 0L;
        while (true)
        {
            var batch = await reader.ReadAfterAsync(partition, position, 5);
            read.AddRange(batch.Entries);
            position = batch.Position;
            if (!batch.HasMore)
            {
                return read;
            }
        }
    }
}
