using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Checkpoints;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.EntityFrameworkCore.Projections;
using Stratara.Orleans.Projections;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A checkpoint is refused rather than misread when it was written under another partition count or
/// by another reader, and the refusal names what differs.
/// </summary>
public sealed class ProjectionCheckpointStoreTests
{
    [Fact]
    public async Task A_checkpoint_written_under_another_partition_count_is_refused_naming_both_counts()
    {
        var store = await StoreWithCheckpointAsync("partition-counter/16");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync("View", 2, "partition-counter/32"));

        Assert.Contains("partition count of 16", refused.Message, StringComparison.Ordinal);
        Assert.Contains("under 32", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_checkpoint_written_by_another_reader_is_refused_naming_both_readers()
    {
        var store = await StoreWithCheckpointAsync("partition-counter/16");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync("View", 2, "postgres-transaction-id/16"));

        Assert.Contains("'partition-counter/16'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("'postgres-transaction-id/16'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_checkpoint_written_by_the_same_reader_is_returned()
    {
        var store = await StoreWithCheckpointAsync("partition-counter/16");

        Assert.Equal(42, await store.GetAsync("View", 2, "partition-counter/16"));
    }

    [Fact]
    public async Task A_first_checkpoint_is_created_where_none_exists_and_one_that_exists_is_kept()
    {
        IFirstCheckpointStore store = await StoreWithCheckpointAsync("partition-counter/16");

        Assert.True(await store.ExistsAsync("View", 2, TestContext.Current.CancellationToken));
        Assert.False(await store.ExistsAsync("View", 3, TestContext.Current.CancellationToken));
        Assert.False(await store.CreateAsync("View", 2, "partition-counter/16", 7, TestContext.Current.CancellationToken));
        Assert.True(await store.CreateAsync("View", 3, "partition-counter/16", 0, TestContext.Current.CancellationToken));
        Assert.True(await store.ExistsAsync("View", 3, TestContext.Current.CancellationToken));
        Assert.False(await store.CreateAsync("View", 3, "partition-counter/16", 9, TestContext.Current.CancellationToken));

        var checkpoints = (IProjectionCheckpointStore)store;
        Assert.Equal(42, await checkpoints.GetAsync("View", 2, "partition-counter/16"));
        Assert.Equal(0, await checkpoints.GetAsync("View", 3, "partition-counter/16"));
    }

    private static async Task<ProjectionCheckpointStore<CheckpointContext>> StoreWithCheckpointAsync(string reader)
    {
        var factory = new ContextFactory($"checkpoints-{Guid.NewGuid():N}");
        await using (var context = factory.CreateDbContext())
        {
            context.Set<ProjectionCheckpoint>().Add(new ProjectionCheckpoint { Projection = "View", Partition = 2, Position = 42, Reader = reader });
            await context.SaveChangesAsync();
        }

        return new ProjectionCheckpointStore<CheckpointContext>(factory);
    }

    public sealed class CheckpointContext(DbContextOptions<CheckpointContext> options) : ReadDbContext<CheckpointContext>(options);

    private sealed class ContextFactory(string database) : IDbContextFactory<CheckpointContext>
    {
        public CheckpointContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<CheckpointContext>().UseInMemoryDatabase(database).Options);
    }
}
