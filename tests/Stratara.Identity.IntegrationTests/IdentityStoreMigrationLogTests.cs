using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stratara.EventSourcing.EntityFrameworkCore.IdentityStore;

namespace Stratara.Identity.IntegrationTests;

/// <summary>
/// <c>identity-store</c> on PostgreSQL: migrating the store logs nothing at <c>Error</c> or above, on a first start
/// as on every later one, so a fresh environment does not stop a log check (NextPA finding F-032).
/// </summary>
public sealed class IdentityStoreMigrationLogTests(IdentityStoreDatabase database) : IClassFixture<IdentityStoreDatabase>
{
    private static string NewDatabase() => $"identity_{Guid.NewGuid():N}";

    [Fact]
    public async Task Migrating_a_database_that_exists_without_the_schema_logs_no_error()
    {
        var identityDatabase = NewDatabase();
        await database.ExecuteAsync("postgres", $"CREATE DATABASE {identityDatabase}");
        var logs = new RecordingLoggerProvider();
        using var host = database.Host(identityDatabase, logs: logs);

        await host.Services.MigrateStrataraIdentityStoreAsync(TestContext.Current.CancellationToken);

        Assert.Empty(logs.AtLeast(LogLevel.Error));
        Assert.Contains(StrataraIdentityStore.MigrationsHistoryTable, await database.ReadAsync(identityDatabase,
            $"SELECT table_name FROM information_schema.tables WHERE table_schema = '{StrataraIdentityStore.Schema}'"));
    }

    [Fact]
    public async Task Migrating_a_database_that_does_not_exist_logs_no_error()
    {
        var logs = new RecordingLoggerProvider();
        using var host = database.Host(NewDatabase(), logs: logs);

        await host.Services.MigrateStrataraIdentityStoreAsync(TestContext.Current.CancellationToken);

        Assert.Empty(logs.AtLeast(LogLevel.Error));
    }

    [Fact]
    public async Task Migrating_a_migrated_store_again_applies_nothing_and_logs_no_error()
    {
        var identityDatabase = NewDatabase();
        await database.ExecuteAsync("postgres", $"CREATE DATABASE {identityDatabase}");
        using (var first = database.Host(identityDatabase))
        {
            await first.Services.MigrateStrataraIdentityStoreAsync(TestContext.Current.CancellationToken);
        }

        var history = $"SELECT migration_id FROM {StrataraIdentityStore.Schema}.\"{StrataraIdentityStore.MigrationsHistoryTable}\"";
        var applied = await database.ReadAsync(identityDatabase, history);
        var logs = new RecordingLoggerProvider();
        using var second = database.Host(identityDatabase, logs: logs);

        await second.Services.MigrateStrataraIdentityStoreAsync(TestContext.Current.CancellationToken);

        Assert.Empty(logs.AtLeast(LogLevel.Error));
        Assert.Equal(applied, await database.ReadAsync(identityDatabase, history));
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> _entries = new();

        public IReadOnlyList<string> AtLeast(LogLevel level) =>
            _entries.Where(e => e.Level >= level).Select(e => $"{e.Level} {e.Category}: {e.Message}").ToList();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(
            string category, ConcurrentQueue<(LogLevel Level, string Category, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((logLevel, category, formatter(state, exception)));
        }
    }
}
