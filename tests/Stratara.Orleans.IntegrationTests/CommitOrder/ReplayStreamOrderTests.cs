using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.CommitOrder;

/// <summary>
/// The range a replay reads, on PostgreSQL. Each entry is appended by a save of its own in reverse version order,
/// so a stream's sequence numbers run against its versions exactly as they do where one save's statement order put
/// them there. The range is read in stream order, and a batch that would end inside the inverted run is extended.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ReplayStreamOrderTests(PostgreSqlFixture postgres)
{
    private const int BucketId = 5;

    [Fact]
    public async Task A_stream_numbered_against_its_versions_is_read_in_version_order_across_a_batch_boundary()
    {
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(
            postgres.ConnectionStringFor("poc_replay_stream_order"), maintainCounter: false);
        await using (var context = await store.CreateContextAsync())
        {
            await context.Set<EventStreamEntry>().ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        var stream = Guid.NewGuid();
        var other = Guid.NewGuid();
        await AppendAsync(store, (other, 1), (stream, 3), (stream, 2), (stream, 1), (other, 2));

        await using var readContext = await store.CreateContextAsync();
        var repository = new EventStreamRepository(readContext);
        var read = new List<EventStreamEntry>();
        var batches = new List<int>();
        var after = 0L;
        while (true)
        {
            var batch = await repository.GetManyAfterSequenceInStreamOrderAsync(after, 2, TestContext.Current.CancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            batches.Add(batch.Count);
            read.AddRange(batch);
            after = batch.Max(e => e.SequenceNumber);
        }

        Assert.Equal([4, 1], batches);
        Assert.Equal(5, read.Select(e => e.Id).Distinct().Count());
        Assert.Equal([1L, 2L, 3L], read.Where(e => e.StreamId == stream).Select(e => e.Version));
        Assert.Equal([1L, 2L], read.Where(e => e.StreamId == other).Select(e => e.Version));
    }

    private static async Task AppendAsync(PocStore<PocCommitOrderWriteDbContext> store, params (Guid Stream, long Version)[] entries)
    {
        var tenantId = Guid.NewGuid();
        foreach (var (stream, version) in entries)
        {
            await using var context = await store.CreateContextAsync();
            context.Set<EventStreamEntry>().Add(PocStore<PocCommitOrderWriteDbContext>.NewEntry(stream, version, BucketId, tenantId));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
    }
}
