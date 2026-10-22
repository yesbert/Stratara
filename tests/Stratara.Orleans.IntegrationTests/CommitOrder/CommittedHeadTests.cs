using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.CommitOrder;

/// <summary>
/// The committed head a mark is taken from: the native reader answers it without the horizon its head is held back to,
/// so an entry committed while another writer is open lies at or below it and above the head; the portable reader's
/// committed head is its head, and an empty partition's is zero under both.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class CommittedHeadTests(PostgreSqlFixture postgres)
{
    public static TheoryData<string> Readers => new() { "postgres-transaction-id", "partition-counter" };

    [Fact]
    public async Task An_entry_committed_while_another_writer_is_open_lies_above_the_head_and_within_the_committed_head()
    {
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(
            postgres.ConnectionStringFor("poc_committed_head_open"), maintainCounter: false);
        var reader = new PostgresTransactionIdReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options));
        var tenantId = Guid.NewGuid();
        var bucketId = Random.Shared.Next(0, 4096);
        var partition = PartitionMap.PartitionOf(bucketId, store.Options.PartitionCount);
        var held = Guid.NewGuid();
        var committed = Guid.NewGuid();

        long head;
        long committedHead;
        await using (var context = await store.CreateContextAsync())
        {
            await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            context.Set<EventStreamEntry>().Add(PocStore<PocCommitOrderWriteDbContext>.NewEntry(held, 1, bucketId, tenantId));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            await AppendAsync(store, committed, bucketId, tenantId);

            head = await reader.HeadAsync(partition, TestContext.Current.CancellationToken);
            committedHead = await reader.CommittedHeadAsync(partition, TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        var all = await reader.ReadAfterAsync(partition, 0, 10_000, TestContext.Current.CancellationToken);
        var position = all.Entries.Single(entry => entry.Entry.StreamId == committed).Position;

        Assert.True(head < position, $"the head {head} already covered the entry at {position}");
        Assert.True(committedHead >= position, $"the committed head {committedHead} lies below the entry at {position}");
    }

    [Fact]
    public async Task The_portable_readers_committed_head_is_its_head()
    {
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(
            postgres.ConnectionStringFor("poc_committed_head_counted"), maintainCounter: true);
        ICommittedPositionReader reader = new PortableCounterReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options));
        var bucketId = Random.Shared.Next(0, 4096);
        var partition = PartitionMap.PartitionOf(bucketId, store.Options.PartitionCount);
        await AppendAsync(store, Guid.NewGuid(), bucketId, Guid.NewGuid());

        var head = await reader.HeadAsync(partition, TestContext.Current.CancellationToken);

        Assert.True(head > 0);
        Assert.Equal(head, await reader.CommittedHeadAsync(partition, TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(Readers), DisableDiscoveryEnumeration = true)]
    public async Task An_empty_partition_has_committed_head_zero(string readerName)
    {
        var counted = readerName == "partition-counter";
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(
            postgres.ConnectionStringFor(counted ? "poc_committed_head_empty_counted" : "poc_committed_head_empty"),
            maintainCounter: counted);
        ICommittedPositionReader reader = counted
            ? new PortableCounterReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options))
            : new PostgresTransactionIdReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options));

        Assert.Equal(0, await reader.CommittedHeadAsync(store.Options.PartitionCount - 1, TestContext.Current.CancellationToken));
    }

    private static async Task AppendAsync(PocStore<PocCommitOrderWriteDbContext> store, Guid streamId, int bucketId, Guid tenantId)
    {
        await using var context = await store.CreateContextAsync();
        context.Set<EventStreamEntry>().Add(PocStore<PocCommitOrderWriteDbContext>.NewEntry(streamId, 1, bucketId, tenantId));
        await context.SaveChangesAsync();
    }
}
