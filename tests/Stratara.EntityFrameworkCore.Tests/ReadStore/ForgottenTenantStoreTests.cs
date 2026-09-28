using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.ForgottenTenants;

namespace Stratara.EventSourcing.EntityFrameworkCore.Tests.ReadStore;

/// <summary>
/// <c>projections</c> → <em>A projection can forget a deleted tenant</em>: the read store keeps each
/// projection's deleted tenants apart, records them idempotently, and empties one projection's record while
/// keeping the others — verified on SQLite. A tenant another writer records first is recorded once and without an
/// error in the log, and a provider without an insert that ignores a conflict still records each tenant once.
/// </summary>
public sealed class ForgottenTenantStoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ForgottenTenantStore<StoreReadContext> _store;

    public ForgottenTenantStoreTests()
    {
        _connection.Open();
        var factory = new ContextFactory(new DbContextOptionsBuilder<StoreReadContext>().UseSqlite(_connection).Options);
        using (var context = factory.CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        _store = new ForgottenTenantStore<StoreReadContext>(factory);
    }

    public void Dispose() => _connection.Dispose();

    private sealed class StoreReadContext(DbContextOptions<StoreReadContext> options)
        : ReadDbContext<StoreReadContext>(options);

    private sealed class ContextFactory(DbContextOptions<StoreReadContext> options) : IDbContextFactory<StoreReadContext>
    {
        public StoreReadContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task A_forgotten_tenant_is_known_to_its_projection_only()
    {
        var tenant = Guid.CreateVersion7();

        await _store.ForgetAsync("Entries", [tenant]);

        Assert.True(await _store.HasForgottenAsync("Entries", tenant));
        Assert.False(await _store.HasForgottenAsync("Customers", tenant));
        Assert.False(await _store.HasForgottenAsync("Entries", Guid.CreateVersion7()));
    }

    [Fact]
    public async Task Forgetting_is_idempotent_across_repeated_and_overlapping_calls()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        await _store.ForgetAsync("Entries", [first]);
        await _store.ForgetAsync("Entries", [first]);
        await _store.ForgetAsync("Entries", [first, second, second]);

        Assert.True(await _store.HasForgottenAsync("Entries", first));
        Assert.True(await _store.HasForgottenAsync("Entries", second));
        await using var context = new StoreReadContext(new DbContextOptionsBuilder<StoreReadContext>().UseSqlite(_connection).Options);
        Assert.Equal(2, await context.Set<ForgottenTenant>().CountAsync());
    }

    [Fact]
    public async Task Clearing_one_projection_keeps_the_others()
    {
        var tenant = Guid.CreateVersion7();
        await _store.ForgetAsync("Entries", [tenant]);
        await _store.ForgetAsync("Customers", [tenant]);

        await _store.ClearAsync("Entries");

        Assert.False(await _store.HasForgottenAsync("Entries", tenant));
        Assert.True(await _store.HasForgottenAsync("Customers", tenant));
    }

    [Fact]
    public async Task A_tenant_another_writer_records_first_is_forgotten_without_an_error()
    {
        // Two deliveries of one projection forget the same tenant at once: the other writer's insert lands between this
        // store's read and its own insert.
        var tenant = Guid.CreateVersion7();
        var logs = new LogCapture();
        var options = new DbContextOptionsBuilder<StoreReadContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new OtherWriterFirst())
            .UseLoggerFactory(LoggerFactory.Create(builder => builder.AddProvider(logs)))
            .Options;
        var store = new ForgottenTenantStore<StoreReadContext>(new ContextFactory(options));

        await store.ForgetAsync("Entries", [tenant]);

        Assert.True(await _store.HasForgottenAsync("Entries", tenant));
        await using var context = new StoreReadContext(new DbContextOptionsBuilder<StoreReadContext>().UseSqlite(_connection).Options);
        Assert.Equal(1, await context.Set<ForgottenTenant>().CountAsync());
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task A_provider_without_an_insert_that_ignores_a_conflict_still_records_each_tenant_once()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var options = new DbContextOptionsBuilder<StoreReadContext>().UseInMemoryDatabase($"forgotten-{Guid.NewGuid():N}").Options;
        var store = new ForgottenTenantStore<StoreReadContext>(new ContextFactory(options));

        await store.ForgetAsync("Entries", [first]);
        await store.ForgetAsync("Entries", [first, second]);

        Assert.True(await store.HasForgottenAsync("Entries", first));
        Assert.True(await store.HasForgottenAsync("Entries", second));
        await using var context = new StoreReadContext(options);
        Assert.Equal(2, await context.Set<ForgottenTenant>().CountAsync());
    }

    /// <summary>Runs the store's insert once before the store does, as a concurrent writer of the same row would.</summary>
    private sealed class OtherWriterFirst : DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await WriteFirstAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await WriteFirstAsync(command, cancellationToken);
            return result;
        }

        private async Task WriteFirstAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (command.CommandText.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
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
}
