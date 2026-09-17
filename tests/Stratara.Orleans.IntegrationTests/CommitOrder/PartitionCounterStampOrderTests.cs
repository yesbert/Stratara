using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.CommitOrder;

/// <summary>
/// One save that adds several versions of a stream out of order, beside another stream, is positioned in version
/// order within each stream, and the portable reader returns the stream in that order (scenario <em>One append holds
/// several versions of one stream</em>).
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class PartitionCounterStampOrderTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task Positions_within_one_save_follow_the_version_order_of_each_stream()
    {
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(postgres.ConnectionStringFor("poc_portable_stamp_order"), maintainCounter: true);
        await using (var reset = await store.CreateContextAsync())
        {
            await reset.Set<EventStreamEntry>().ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            await reset.Set<Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder.PartitionPosition>()
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.Position, 0), TestContext.Current.CancellationToken);
        }

        const int bucket = 9;
        var partition = PartitionMap.PartitionOf(bucket, store.Options.PartitionCount);
        var stream = Guid.NewGuid();
        var other = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        await using (var context = await store.CreateContextAsync())
        {
            foreach (var (streamId, version) in new[] { (stream, 3L), (other, 2L), (stream, 1L), (other, 1L), (stream, 2L) })
            {
                context.Set<EventStreamEntry>().Add(PocStore<PocCommitOrderWriteDbContext>.NewEntry(streamId, version, bucket, tenant));
            }

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var reader = new PortableCounterReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options));
        var read = await reader.ReadAfterAsync(partition, 0, 100, TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L, 3L], read.Entries.Where(entry => entry.Entry.StreamId == stream).Select(entry => entry.Entry.Version));
        Assert.Equal([1L, 2L], read.Entries.Where(entry => entry.Entry.StreamId == other).Select(entry => entry.Entry.Version));
        Assert.Equal([stream, stream, stream, other, other], read.Entries.Select(entry => entry.Entry.StreamId));
    }
}
