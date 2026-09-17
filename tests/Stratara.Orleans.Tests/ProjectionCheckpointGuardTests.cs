using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.Projections;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Checkpoints;
using Stratara.Orleans.EntityFrameworkCore.Projections;

namespace Stratara.Orleans.Tests;

/// <summary>
/// Two stores over one database stand for two activations of one reader: an activation that outlived its successor
/// cannot rewind what the successor wrote, and no write changes the reader a checkpoint was written under — except a
/// reset, which returns the row to the beginning under the resetting host's own reader whatever wrote it, so a
/// rebuild or a replay recovers from the refusal inside the running cluster (scenarios <em>A stale activation writes
/// a checkpoint</em>, <em>A checkpoint is written under another reader's name</em>, <em>A read model is rebuilt after
/// the partition count changed</em>).
/// </summary>
public sealed class ProjectionCheckpointGuardTests : IAsyncLifetime
{
    private const string Reader = "partition-counter/16";
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        await using var context = new ContextFactory(_connection).CreateDbContext();
        await context.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task A_stale_activation_is_refused_naming_both_positions_and_the_successor_stands()
    {
        var stale = Store();
        var successor = Store();
        await stale.AdvanceAsync("View", 2, Reader, 0, 10);
        await successor.AdvanceAsync("View", 2, Reader, 10, 20);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => stale.AdvanceAsync("View", 2, Reader, 10, 15));

        Assert.Contains("at 20", refused.Message, StringComparison.Ordinal);
        Assert.Contains("not at 10", refused.Message, StringComparison.Ordinal);
        Assert.Equal(20, await successor.GetAsync("View", 2, Reader));
    }

    [Fact]
    public async Task A_reset_takes_over_a_checkpoint_written_under_another_reader_and_partition_count()
    {
        var store = Store();
        await store.AdvanceAsync("View", 2, Reader, 0, 42);

        await store.ResetAsync("View", 2, "postgres-transaction-id/8");

        Assert.Equal(0, await store.GetAsync("View", 2, "postgres-transaction-id/8"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync("View", 2, Reader));
    }

    [Fact]
    public async Task A_reset_of_a_checkpoint_that_was_never_written_inserts_the_beginning()
    {
        var store = Store();

        await store.ResetAsync("View", 3, Reader);

        Assert.Equal(0, await store.GetAsync("View", 3, Reader));
    }

    [Fact]
    public async Task Two_first_advances_from_nothing_leave_the_winner_and_refuse_the_other()
    {
        var first = Store();
        var second = Store();
        await first.AdvanceAsync("View", 2, Reader, 0, 10);

        await Assert.ThrowsAsync<InvalidOperationException>(() => second.AdvanceAsync("View", 2, Reader, 0, 7));

        Assert.Equal(10, await first.GetAsync("View", 2, Reader));
    }

    [Fact]
    public async Task An_advance_under_another_reader_is_refused_naming_both_readers()
    {
        var store = Store();
        await store.AdvanceAsync("View", 2, Reader, 0, 10);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => store.AdvanceAsync("View", 2, "postgres-transaction-id/16", 10, 20));

        Assert.Contains("'partition-counter/16'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("'postgres-transaction-id/16'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(10, await store.GetAsync("View", 2, Reader));
    }

    [Fact]
    public async Task A_replacement_under_another_reader_is_refused_naming_both_readers()
    {
        var store = Store();
        await store.SetAsync("View", 2, Reader, 10);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync("View", 2, "postgres-transaction-id/16", 0));

        Assert.Contains("'partition-counter/16'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("'postgres-transaction-id/16'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(10, await store.GetAsync("View", 2, Reader));
    }

    [Fact]
    public async Task A_replacement_under_the_same_reader_still_resets_the_position()
    {
        var store = Store();
        await store.AdvanceAsync("View", 2, Reader, 0, 10);

        await store.SetAsync("View", 2, Reader, 0);

        Assert.Equal(0, await store.GetAsync("View", 2, Reader));
    }

    [Fact]
    public async Task Concurrent_replacements_of_a_checkpoint_never_written_all_succeed()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Store().SetAsync("View", 2, Reader, 0)));

        Assert.Equal(0, await Store().GetAsync("View", 2, Reader));
    }

    [Fact]
    public async Task The_default_advance_of_a_consumers_own_store_replaces_the_position()
    {
        IProjectionCheckpointStore store = new ReplacingStore();

        await store.AdvanceAsync("View", 2, Reader, 3, 10);

        Assert.Equal(10, ((ReplacingStore)store).Written);
    }

    private ProjectionCheckpointStore<CheckpointContext> Store() => new(new ContextFactory(_connection));

    public sealed class CheckpointContext(DbContextOptions<CheckpointContext> options) : ReadDbContext<CheckpointContext>(options);

    private sealed class ContextFactory(SqliteConnection connection) : IDbContextFactory<CheckpointContext>
    {
        public CheckpointContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<CheckpointContext>().UseSqlite(connection).Options);
    }

    private sealed class ReplacingStore : IProjectionCheckpointStore
    {
        public long Written { get; private set; }

        public Task<long> GetAsync(string projection, int partition, string reader, CancellationToken cancellationToken = default) => Task.FromResult(Written);

        public Task SetAsync(string projection, int partition, string reader, long position, CancellationToken cancellationToken = default)
        {
            Written = position;
            return Task.CompletedTask;
        }
    }
}
