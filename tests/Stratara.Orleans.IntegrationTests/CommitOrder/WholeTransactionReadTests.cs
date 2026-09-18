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
/// Scenario <em>One transaction holds more entries than a batch</em>: the native reader never ends a batch inside one
/// transaction's entries. It cuts before a transaction that would straddle the batch, holds a transaction back whole
/// when the batch ends inside it, returns a transaction larger than the batch whole — a batch larger than the
/// batch size — and cuts at the batch size where the transactions of a batch fit inside it.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class WholeTransactionReadTests(PostgreSqlFixture postgres)
{
    private const int BatchSize = 3;

    [Fact]
    public async Task A_transaction_larger_than_the_batch_is_held_back_whole_and_then_returned_whole()
    {
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(postgres.ConnectionStringFor("poc_whole_transaction"), maintainCounter: false);
        var (reader, partition, bucket, position) = await ReaderAtHeadAsync(store);
        var single = await CommitAsync(store, bucket, 1);
        var large = await CommitAsync(store, bucket, 5);
        var last = await CommitAsync(store, bucket, 1);

        var first = await reader.ReadAfterAsync(partition, position, BatchSize, TestContext.Current.CancellationToken);
        var second = await reader.ReadAfterAsync(partition, first.Position, BatchSize, TestContext.Current.CancellationToken);
        var third = await reader.ReadAfterAsync(partition, second.Position, BatchSize, TestContext.Current.CancellationToken);

        Assert.Equal(single, Ids(first));
        Assert.True(first.HasMore);
        Assert.Equal(large, Ids(second));
        Assert.True(second.Entries.Count > BatchSize, "the transaction larger than the batch was not returned whole");
        Assert.True(second.HasMore);
        Assert.Equal(last, Ids(third));
        Assert.False(third.HasMore);
    }

    [Fact]
    public async Task A_transaction_that_would_straddle_the_batch_starts_the_next_one()
    {
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(postgres.ConnectionStringFor("poc_whole_transaction"), maintainCounter: false);
        var (reader, partition, bucket, position) = await ReaderAtHeadAsync(store);
        var transactions = new[] { await CommitAsync(store, bucket, 2), await CommitAsync(store, bucket, 2), await CommitAsync(store, bucket, 2) };

        for (var i = 0; i < transactions.Length; i++)
        {
            var batch = await reader.ReadAfterAsync(partition, position, BatchSize, TestContext.Current.CancellationToken);

            Assert.Equal(transactions[i], Ids(batch));
            Assert.Equal(i < transactions.Length - 1, batch.HasMore);
            position = batch.Position;
        }
    }

    [Fact]
    public async Task Transactions_that_fit_are_cut_at_the_batch_size()
    {
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(postgres.ConnectionStringFor("poc_whole_transaction"), maintainCounter: false);
        var (reader, partition, bucket, position) = await ReaderAtHeadAsync(store);
        var committed = new List<Guid>();
        for (var i = 0; i < BatchSize + 2; i++)
        {
            committed.AddRange(await CommitAsync(store, bucket, 1));
        }

        var first = await reader.ReadAfterAsync(partition, position, BatchSize, TestContext.Current.CancellationToken);
        var second = await reader.ReadAfterAsync(partition, first.Position, BatchSize, TestContext.Current.CancellationToken);

        Assert.Equal(committed.Take(BatchSize), Ids(first));
        Assert.True(first.HasMore);
        Assert.Equal(committed.Skip(BatchSize), Ids(second));
        Assert.False(second.HasMore);
    }

    private static async Task<(ICommittedPositionReader Reader, int Partition, int Bucket, long Position)> ReaderAtHeadAsync(PocStore<PocCommitOrderWriteDbContext> store)
    {
        var reader = new PostgresTransactionIdReader<PocCommitOrderWriteDbContext>(store.ContextFactory, Options.Create(store.Options));
        var bucket = Random.Shared.Next(0, 4096);
        var partition = PartitionMap.PartitionOf(bucket, store.Options.PartitionCount);
        return (reader, partition, bucket, await reader.HeadAsync(partition, TestContext.Current.CancellationToken));
    }

    private static async Task<Guid[]> CommitAsync(PocStore<PocCommitOrderWriteDbContext> store, int bucket, int entries)
    {
        var tenantId = Guid.NewGuid();
        var added = Enumerable.Range(0, entries).Select(_ => PocStore<PocCommitOrderWriteDbContext>.NewEntry(Guid.NewGuid(), 1, bucket, tenantId)).ToList();
        await using var context = await store.CreateContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        context.Set<EventStreamEntry>().AddRange(added);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return [.. added.Select(entry => entry.Id)];
    }

    private static Guid[] Ids(CommittedBatch batch) => [.. batch.Entries.Select(entry => entry.Entry.Id)];
}
