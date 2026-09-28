using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Checkpoints;
using Stratara.Orleans.EntityFrameworkCore.Projections;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Timers;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// Two writers of the same first checkpoint — an activation and its successor overlapping during a
/// failover — both find no row and both insert. The one that loses to the key takes the row over
/// instead of failing, and leaves no failed statement in the log.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class CheckpointFirstWriteTests(PostgreSqlFixture postgres)
{
    private const string Database = "poc_checkpoint_first_write";

    [Fact]
    public async Task A_first_write_that_loses_the_insert_to_another_writer_updates_the_row_instead_of_failing()
    {
        var connectionString = postgres.ConnectionStringFor(Database);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(connectionString);
        var plain = new ContextFactory(connectionString);
        await using (var context = plain.CreateDbContext())
        {
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        var projection = $"first-write-{Guid.NewGuid():N}";
        var logs = new LogCapture();
        var racing = new ContextFactory(connectionString, logs, new InsertFirst(plain, projection));
        var store = new ProjectionCheckpointStore<PocReadDbContext>(racing);

        await store.SetAsync(projection, 3, "partition-counter/16", 42, TestContext.Current.CancellationToken);

        Assert.Equal(42, await new ProjectionCheckpointStore<PocReadDbContext>(plain).GetAsync(projection, 3, "partition-counter/16", TestContext.Current.CancellationToken));
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
    }

    /// <summary>Inserts the checkpoint through another context just before the store's own insert runs.</summary>
    private sealed class InsertFirst(ContextFactory other, string projection) : DbCommandInterceptor
    {
        private int _done;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await InsertFirstAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await InsertFirstAsync(command, cancellationToken);
            return result;
        }

        private async Task InsertFirstAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (command.CommandText.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref _done, 1) == 0)
            {
                await using var context = other.CreateDbContext();
                context.Set<ProjectionCheckpoint>().Add(new ProjectionCheckpoint { Projection = projection, Partition = 3, Position = 7, Reader = "partition-counter/16" });
                await context.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private sealed class ContextFactory(string connectionString, ILoggerProvider? logs = null, params IInterceptor[] interceptors) : IDbContextFactory<PocReadDbContext>
    {
        private readonly ILoggerFactory? _loggerFactory = logs is null ? null : LoggerFactory.Create(builder => builder.AddProvider(logs));

        public PocReadDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PocReadDbContext>()
                .UseSnakeCaseNamingConvention()
                .UseNpgsql(connectionString)
                .AddInterceptors(interceptors);
            if (_loggerFactory is not null)
            {
                options.UseLoggerFactory(_loggerFactory);
            }

            return new PocReadDbContext(options.Options);
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
}
