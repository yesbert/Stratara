using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Stratara.EventSourcing.EntityFrameworkCore.Tests;

/// <summary>
/// <c>event-sourcing-store</c> → a cancellation is honoured until the store begins to commit, and a commit once begun
/// runs to its end — so a save the store committed is never reported as cancelled. Against SQLite, which honours a
/// cancellation requested at the commit by not committing: the difference the interceptor makes is visible.
/// </summary>
public sealed class CommitCompletionInterceptorTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public sealed class Note
    {
        public int Id { get; set; }

        public string Text { get; set; } = string.Empty;
    }

    private sealed class NotesContext(DbContextOptions<NotesContext> options) : DbContext(options), Stratara.EventSourcing.EntityFrameworkCore.Abstractions.IDbContext
    {
        public DbSet<Note> Notes => Set<Note>();
    }

    /// <summary>Requests the cancellation the moment the commit begins, as a stopping host would.</summary>
    private sealed class CancelsWhenTheCommitBegins(CancellationTokenSource stop) : DbTransactionInterceptor
    {
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            await stop.CancelAsync();
            return result;
        }
    }

    /// <summary>Requests the cancellation while the changes are written, before any commit.</summary>
    private sealed class CancelsWhileTheChangesAreWritten(CancellationTokenSource stop) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await stop.CancelAsync();
            return result;
        }
    }

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        await using var context = Context();
        await context.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private NotesContext Context(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<NotesContext>().UseSqlite(_connection).AddInterceptors(interceptors).Options);

    private async Task<int> CountAsync()
    {
        await using var context = Context();
        return await context.Notes.CountAsync();
    }

    [Fact]
    public async Task A_cancellation_requested_once_the_commit_has_begun_does_not_stop_it()
    {
        using var stop = new CancellationTokenSource();
        await using (var context = Context(new CancelsWhenTheCommitBegins(stop), CommitCompletionInterceptor.Instance))
        {
            context.Notes.Add(new Note { Text = "committed" });

            await context.SaveChangesAsync(stop.Token);
        }

        Assert.True(stop.IsCancellationRequested);
        Assert.Equal(1, await CountAsync());
    }

    /// <summary>What the interceptor changes: without it the same cancellation abandons the commit.</summary>
    [Fact]
    public async Task Without_it_the_same_cancellation_abandons_the_commit()
    {
        using var stop = new CancellationTokenSource();
        await using (var context = Context(new CancelsWhenTheCommitBegins(stop)))
        {
            context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
            context.Notes.Add(new Note { Text = "abandoned" });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.SaveChangesAsync(stop.Token));
        }

        Assert.Equal(0, await CountAsync());
    }

    /// <summary>A single statement would otherwise commit on its own, with no commit to let run; it gets a transaction.</summary>
    [Fact]
    public async Task A_save_of_a_single_statement_runs_in_a_transaction_whose_commit_runs_to_its_end()
    {
        using var stop = new CancellationTokenSource();
        await using (var context = Context(new CancelsWhenTheCommitBegins(stop), CommitCompletionInterceptor.Instance))
        {
            context.Notes.Add(new Note { Text = "one statement" });

            await context.SaveChangesAsync(stop.Token);

            Assert.Equal(AutoTransactionBehavior.Always, context.Database.AutoTransactionBehavior);
        }

        Assert.True(stop.IsCancellationRequested);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task A_cancellation_requested_while_the_changes_are_written_still_writes_nothing()
    {
        using var stop = new CancellationTokenSource();
        await using (var context = Context(new CancelsWhileTheChangesAreWritten(stop), CommitCompletionInterceptor.Instance))
        {
            context.Notes.Add(new Note { Text = "never" });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.SaveChangesAsync(stop.Token));
        }

        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task An_explicit_transaction_commits_to_its_end_as_well()
    {
        using var stop = new CancellationTokenSource();
        await using (var context = Context(new CancelsWhenTheCommitBegins(stop), CommitCompletionInterceptor.Instance))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(stop.Token);
            context.Notes.Add(new Note { Text = "committed" });
            await context.SaveChangesAsync(stop.Token);

            await transaction.CommitAsync(stop.Token);
        }

        Assert.Equal(1, await CountAsync());
    }

    private sealed class NotesUnitOfWork(IDbContextFactory<NotesContext> factory) : UnitOfWork<NotesContext>(factory)
    {
        public static NotesContext ContextOf(Stratara.Abstractions.Persistence.ITransaction transaction) => GetDbContext(transaction);
    }

    private sealed class NotesFactory(DbContextOptions<NotesContext> options) : IDbContextFactory<NotesContext>
    {
        public NotesContext CreateDbContext() => new(options);
    }

    /// <summary>
    /// The framework's unit of work commits without the caller's token on a context that does not carry the
    /// interceptor — a write context a host registered itself — and gives even a single statement a transaction to
    /// commit.
    /// </summary>
    [Fact]
    public async Task The_unit_of_work_lets_a_commit_run_to_its_end_on_a_context_without_the_interceptor()
    {
        using var stop = new CancellationTokenSource();
        var unitOfWork = new NotesUnitOfWork(new NotesFactory(
            new DbContextOptionsBuilder<NotesContext>().UseSqlite(_connection).AddInterceptors(new CancelsWhenTheCommitBegins(stop)).Options));

        await using (var transaction = await unitOfWork.StartAsync())
        {
            NotesUnitOfWork.ContextOf(transaction).Notes.Add(new Note { Text = "one statement" });

            await transaction.SaveChangesAsync(stop.Token);
        }

        Assert.True(stop.IsCancellationRequested);
        Assert.Equal(1, await CountAsync());
    }
}
