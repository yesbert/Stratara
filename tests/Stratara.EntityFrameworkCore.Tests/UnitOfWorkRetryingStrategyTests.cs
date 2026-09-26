using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Stratara.Abstractions.Persistence;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

namespace Stratara.EventSourcing.EntityFrameworkCore.Tests;

/// <summary>
/// The unit of work under an execution strategy that retries on failure: a save runs as one retriable unit — a
/// transaction of its own, the changes accepted only after the commit — so a transient failure runs it again whole,
/// and a context whose strategy does not retry saves as it always did. On SQLite.
/// </summary>
public sealed class UnitOfWorkRetryingStrategyTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public sealed class Note
    {
        public int Id { get; set; }

        public string Text { get; set; } = string.Empty;
    }

    private sealed class NotesContext(DbContextOptions<NotesContext> options) : DbContext(options), IDbContext
    {
        public DbSet<Note> Notes => Set<Note>();
    }

    private sealed class TransientProbeException() : Exception("a transient failure");

    private sealed class RetryingStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(10))
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TransientProbeException;
    }

    /// <summary>Counts the commits, and fails the first insert once with a transient failure when asked to.</summary>
    private sealed class Observer(bool failFirstInsert) : DbCommandInterceptor, IDbTransactionInterceptor
    {
        private bool _failed;

        public int Commits { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (failFirstInsert && !_failed && command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase))
            {
                _failed = true;
                throw new TransientProbeException();
            }

            return ValueTask.FromResult(result);
        }

        public ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Commits++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class NotesFactory(DbContextOptions<NotesContext> options) : IDbContextFactory<NotesContext>
    {
        public NotesContext CreateDbContext() => new(options);
    }

    private sealed class NotesUnitOfWork(IDbContextFactory<NotesContext> factory) : UnitOfWork<NotesContext>(factory)
    {
        public static NotesContext ContextOf(ITransaction transaction) => GetDbContext(transaction);
    }

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        await using var context = new NotesContext(new DbContextOptionsBuilder<NotesContext>().UseSqlite(_connection).Options);
        await context.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private NotesUnitOfWork UnitOfWork(Observer observer, bool retrying) =>
        new(new NotesFactory(new DbContextOptionsBuilder<NotesContext>()
            .UseSqlite(_connection, sqlite =>
            {
                if (retrying)
                {
                    sqlite.ExecutionStrategy(dependencies => new RetryingStrategy(dependencies));
                }
            })
            .AddInterceptors(observer)
            .Options));

    private async Task SaveOneNoteAsync(NotesUnitOfWork unitOfWork)
    {
        await using var transaction = await unitOfWork.StartAsync(TestContext.Current.CancellationToken);
        NotesUnitOfWork.ContextOf(transaction).Notes.Add(new Note { Text = "one" });
        await transaction.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> CountAsync()
    {
        await using var context = new NotesContext(new DbContextOptionsBuilder<NotesContext>().UseSqlite(_connection).Options);
        return await context.Notes.CountAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Under_a_retrying_strategy_a_save_runs_in_a_transaction_of_its_own_and_commits()
    {
        var observer = new Observer(failFirstInsert: false);

        await SaveOneNoteAsync(UnitOfWork(observer, retrying: true));

        Assert.Equal(1, observer.Commits);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task Under_a_retrying_strategy_a_transient_failure_runs_the_whole_save_again_once()
    {
        var observer = new Observer(failFirstInsert: true);

        await SaveOneNoteAsync(UnitOfWork(observer, retrying: true));

        Assert.Equal(1, observer.Commits);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task Without_a_retrying_strategy_a_single_statement_saves_as_it_always_did()
    {
        var observer = new Observer(failFirstInsert: false);

        await SaveOneNoteAsync(UnitOfWork(observer, retrying: false));

        Assert.Equal(0, observer.Commits);
        Assert.Equal(1, await CountAsync());
    }
}
