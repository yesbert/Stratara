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
/// Scenario <em>A reader reports its head</em>: both readers answer a partition's head in one query; a read after
/// it returns nothing committed before the head was asked for and everything committed afterwards; an empty
/// partition's head is zero.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ReaderHeadTests(PostgreSqlFixture postgres)
{
    private const int Before = 3;

    public static TheoryData<string> Readers => new() { "postgres-transaction-id", "partition-counter" };

    [Theory]
    [MemberData(nameof(Readers), DisableDiscoveryEnumeration = true)]
    public async Task A_read_after_the_head_returns_only_what_committed_afterwards(string readerName)
    {
        var counted = readerName == "partition-counter";
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(
            postgres.ConnectionStringFor(counted ? "poc_reader_head_counted" : "poc_reader_head"),
            options => options.MaintainPartitionCounter = counted);
        ICommittedPositionReader reader = counted
            ? new PortableCounterReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options))
            : new PostgresTransactionIdReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options));
        var tenantId = Guid.NewGuid();
        var bucketId = Random.Shared.Next(0, 4096);
        var partition = PartitionMap.PartitionOf(bucketId, store.Options.PartitionCount);
        var streamId = Guid.NewGuid();
        var alreadyThere = (await reader.ReadAfterAsync(partition, 0, 10_000)).Entries.Count;

        for (var version = 1; version <= Before; version++)
        {
            await AppendAsync(store, streamId, version, bucketId, tenantId);
        }

        var head = await reader.HeadAsync(partition);
        var after = Guid.NewGuid();
        await AppendAsync(store, after, 1, bucketId, tenantId);

        var afterHead = await reader.ReadAfterAsync(partition, head, 10_000);
        var fromStart = await reader.ReadAfterAsync(partition, 0, 10_000);
        Assert.True(head > 0, "the head of a partition with entries was zero");
        Assert.Equal([after], afterHead.Entries.Select(entry => entry.Entry.StreamId).Distinct());
        Assert.Equal(alreadyThere + Before + 1, fromStart.Entries.Count);
        Assert.Equal(head, await reader.HeadAsync(partition) is var later && later >= head ? head : later);
    }

    [Theory]
    [MemberData(nameof(Readers), DisableDiscoveryEnumeration = true)]
    public async Task An_empty_partition_has_head_zero(string readerName)
    {
        var counted = readerName == "partition-counter";
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(
            postgres.ConnectionStringFor(counted ? "poc_reader_head_empty_counted" : "poc_reader_head_empty"),
            options => options.MaintainPartitionCounter = counted);
        ICommittedPositionReader reader = counted
            ? new PortableCounterReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options))
            : new PostgresTransactionIdReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options));

        Assert.Equal(0, await reader.HeadAsync(store.Options.PartitionCount - 1));
    }

    private static async Task AppendAsync(PocStore<PocCommitOrderWriteDbContext> store, Guid streamId, long version, int bucketId, Guid tenantId)
    {
        await using var context = await store.CreateContextAsync();
        context.Set<EventStreamEntry>().Add(PocStore<PocCommitOrderWriteDbContext>.NewEntry(streamId, version, bucketId, tenantId));
        await context.SaveChangesAsync();
    }
}
