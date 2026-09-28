using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
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
/// the partition count changed</em>). A writer whose first insert loses to another's takes the other's row over, or is
/// refused, without a failed statement in the log.
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

    [Fact]
    public async Task A_first_checkpoint_whose_insert_loses_to_another_writer_writes_nothing_and_the_winner_stands()
    {
        var winner = Store();
        var logs = new LogCapture();
        var loser = new ProjectionCheckpointStore<CheckpointContext>(
            new ContextFactory(_connection, new BeforeTheFirstInsert(() => winner.CreateAsync("View", 2, Reader, 30)), logs));

        var created = await loser.CreateAsync("View", 2, Reader, 50);

        Assert.False(created);
        Assert.Equal(30, await winner.FindAsync("View", 2, Reader));
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task A_replacement_whose_first_insert_loses_to_another_writer_takes_the_row_over()
    {
        var winner = Store();
        var logs = new LogCapture();
        var loser = new ProjectionCheckpointStore<CheckpointContext>(
            new ContextFactory(_connection, new BeforeTheFirstInsert(() => winner.CreateAsync("View", 2, Reader, 30)), logs));

        await loser.SetAsync("View", 2, Reader, 50);

        Assert.Equal(50, await winner.GetAsync("View", 2, Reader));
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task A_first_advance_whose_insert_loses_to_another_writer_is_refused_naming_both_positions()
    {
        var winner = Store();
        var logs = new LogCapture();
        var loser = new ProjectionCheckpointStore<CheckpointContext>(
            new ContextFactory(_connection, new BeforeTheFirstInsert(() => winner.CreateAsync("View", 2, Reader, 30)), logs));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => loser.AdvanceAsync("View", 2, Reader, 0, 50));

        Assert.Contains("is at 30, not at 0", refused.Message, StringComparison.Ordinal);
        Assert.Equal(30, await winner.GetAsync("View", 2, Reader));
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task A_reset_whose_insert_loses_to_another_writer_takes_the_row_over_under_its_own_reader()
    {
        var winner = Store();
        var logs = new LogCapture();
        var loser = new ProjectionCheckpointStore<CheckpointContext>(
            new ContextFactory(_connection, new BeforeTheFirstInsert(() => winner.CreateAsync("View", 2, "postgres-transaction-id/16", 30)), logs));

        await loser.ResetAsync("View", 2, Reader);

        Assert.Equal(0, await winner.GetAsync("View", 2, Reader));
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task A_checkpoint_is_found_where_it_exists_at_the_beginning_and_not_where_it_does_not()
    {
        var store = Store();
        await store.ResetAsync("View", 2, Reader);

        Assert.Equal(0, await store.FindAsync("View", 2, Reader));
        Assert.Null(await store.FindAsync("View", 3, Reader));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FindAsync("View", 2, "postgres-transaction-id/8"));
    }

    private ProjectionCheckpointStore<CheckpointContext> Store() => new(new ContextFactory(_connection));

    public sealed class CheckpointContext(DbContextOptions<CheckpointContext> options) : ReadDbContext<CheckpointContext>(options);

    private sealed class ContextFactory(SqliteConnection connection, IInterceptor? interceptor = null, ILoggerProvider? logs = null) : IDbContextFactory<CheckpointContext>
    {
        private readonly ILoggerFactory? _loggerFactory = logs is null ? null : LoggerFactory.Create(builder => builder.AddProvider(logs));

        public CheckpointContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<CheckpointContext>().UseSqlite(connection);
            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }

            if (_loggerFactory is not null)
            {
                options.UseLoggerFactory(_loggerFactory);
            }

            return new CheckpointContext(options.Options);
        }
    }

    /// <summary>Lets another writer commit first, once, just before the intercepted context's first insert runs.</summary>
    private sealed class BeforeTheFirstInsert(Func<Task> other) : DbCommandInterceptor
    {
        private int _done;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await OtherFirstAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await OtherFirstAsync(command);
            return result;
        }

        private async Task OtherFirstAsync(DbCommand command)
        {
            if (command.CommandText.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref _done, 1) == 0)
            {
                await other();
            }
        }
    }

    private sealed class LogCapture : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, int EventId, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(Entries);

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<(LogLevel Level, int EventId, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((logLevel, eventId.Id, formatter(state, exception)));
        }
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
