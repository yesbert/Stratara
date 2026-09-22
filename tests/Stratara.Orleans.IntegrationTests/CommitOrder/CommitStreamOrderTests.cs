using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.CommitOrder;

/// <summary>
/// A save that creates a stream and appends a further version of it commits both entries under one
/// transaction. The identity the database hands out follows the order it inserted the rows in, which
/// is not the order the entries were appended in, so a reader that returns them by identity can hand
/// a projection the second fact before the first. Both readers return each stream's entries in
/// version order instead.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class CommitStreamOrderTests(PostgreSqlFixture postgres)
{
    private const string Database = "poc_commit_stream_order";

    [Fact]
    public async Task The_native_reader_returns_a_streams_entries_in_version_order()
    {
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(
            postgres.ConnectionStringFor(Database + "_native"),
            maintainCounter: false);
        var reader = new PostgresTransactionIdReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options));

        var bucketId = Random.Shared.Next(0, 4096);
        var partition = PartitionMap.PartitionOf(bucketId, store.Options.PartitionCount);
        var before = (await DrainAsync(reader, partition, 0)).Position;
        var (streamId, otherStreamId) = await WriteOneCommitAsync(store, bucketId);

        var read = (await DrainAsync(reader, partition, before)).Entries;

        AssertVersionOrder(read, streamId);
        AssertVersionOrder(read, otherStreamId);
    }

    [Fact]
    public async Task The_portable_reader_returns_a_streams_entries_in_version_order()
    {
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(
            postgres.ConnectionStringFor(Database + "_portable"));
        var reader = new PortableCounterReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options));

        var bucketId = Random.Shared.Next(0, 4096);
        var partition = PartitionMap.PartitionOf(bucketId, store.Options.PartitionCount);
        var before = (await DrainAsync(reader, partition, 0)).Position;
        var (streamId, otherStreamId) = await WriteOneCommitAsync(store, bucketId);

        var read = (await DrainAsync(reader, partition, before)).Entries;

        AssertVersionOrder(read, streamId);
        AssertVersionOrder(read, otherStreamId);
    }

    /// <summary>
    /// One save holding two versions of one stream and two of another, each stream's later version
    /// added first — the order a handler does not control and EF Core does not promise.
    /// </summary>
    private static async Task<(Guid StreamId, Guid OtherStreamId)> WriteOneCommitAsync(
        PocStore<PocCommitOrderWriteDbContext> store, int bucketId)
    {
        var streamId = Guid.NewGuid();
        var otherStreamId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        await using var context = await store.CreateContextAsync();
        context.Set<EventStreamEntry>().AddRange(
            PocStore<PocCommitOrderWriteDbContext>.NewEntry(streamId, 2, bucketId, tenantId),
            PocStore<PocCommitOrderWriteDbContext>.NewEntry(otherStreamId, 2, bucketId, tenantId),
            PocStore<PocCommitOrderWriteDbContext>.NewEntry(streamId, 1, bucketId, tenantId),
            PocStore<PocCommitOrderWriteDbContext>.NewEntry(otherStreamId, 1, bucketId, tenantId));
        await context.SaveChangesAsync();

        return (streamId, otherStreamId);
    }

    private static void AssertVersionOrder(IReadOnlyList<EventStreamEntry> read, Guid streamId)
    {
        var versions = read.Where(entry => entry.StreamId == streamId).Select(entry => entry.Version).ToList();

        Assert.Equal([1L, 2L], versions);
    }

    private static async Task<(long Position, IReadOnlyList<EventStreamEntry> Entries)> DrainAsync(
        ICommittedPositionReader reader, int partition, long position)
    {
        var read = new List<EventStreamEntry>();
        while (true)
        {
            var batch = await reader.ReadAfterAsync(partition, position, 100);
            if (batch.Entries.Count == 0)
            {
                return (batch.Position, read);
            }

            read.AddRange(batch.Entries.Select(entry => entry.Entry));
            position = batch.Position;
        }
    }
}
