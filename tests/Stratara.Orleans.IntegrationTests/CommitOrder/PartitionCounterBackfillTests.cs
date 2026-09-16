using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.CommitOrder;

/// <summary>
/// Task 2.4: a store that holds entries from before it adopted the partition counter. A host reading
/// with the portable reader refuses to start and names the backfill; the backfill positions the old
/// entries in the order they were appended, ahead of the ones appended since; a host started afterwards reads every entry of a
/// partition in order.
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
        await using var before = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, Configure(maintainCounter: false));
        await using (var context = await before.CreateContextAsync())
        {
            await context.Set<EventStreamEntry>().ExecuteDeleteAsync();
            await context.Database.ExecuteSqlRawAsync("UPDATE partition_position SET position = 0");
        }

        // Buckets fall while sequence numbers rise, so an order by bucket and an order of appending disagree in every partition.
        var history = await AppendAsync(before, count: 12, bucketOf: i => (12 - i) * 7);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => StartHostAsync(connectionString));
        Assert.Contains(nameof(PartitionCounterBackfill), refused.Message, StringComparison.Ordinal);

        await using var after = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, Configure(maintainCounter: true));
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

            Assert.Equal(expectedHistory.Concat(expectedLive), read.Select(e => e.Entry.Id));
            Assert.True(read.Select(e => e.Position).SequenceEqual(read.Select(e => e.Position).Order()), $"partition {partition} is not in position order");
            Assert.Equal(read.Count, read.Select(e => e.Position).Distinct().Count());
        }

        await host.StopAsync();
    }

    private static Action<CommitOrderOptions> Configure(bool maintainCounter) => options =>
    {
        options.PartitionCount = PartitionCount;
        options.MaintainPartitionCounter = maintainCounter;
    };

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
            .Configure(Configure(maintainCounter: true))
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
