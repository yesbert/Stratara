using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stratara.EventSourcing.EntityFrameworkCore.IdentityStore;

namespace Stratara.Identity.IntegrationTests;

/// <summary>
/// <c>identity-store</c> on PostgreSQL: migrating the store logs nothing at <c>Error</c> or above — on a first start,
/// with or without the database in place, on every later start, and when several hosts start at once — so a fresh
/// environment does not trip a log check on a migration that in fact succeeded.
/// </summary>
public sealed class IdentityStoreMigrationLogTests(IdentityStoreDatabase database) : IClassFixture<IdentityStoreDatabase>
{
    private static string NewDatabase() => $"identity_{Guid.NewGuid():N}";

    private static readonly string History =
        $"SELECT migration_id FROM {StrataraIdentityStore.Schema}.\"{StrataraIdentityStore.MigrationsHistoryTable}\" ORDER BY migration_id";

    // The recorder must have seen the migration at all, or an empty Error list proves nothing.
    private static void AssertNoErrorWhileMigrating(RecordingLoggerProvider logs)
    {
        Assert.NotEmpty(logs.InCategory("Microsoft.EntityFrameworkCore.Migrations"));
        Assert.Empty(logs.AtLeast(LogLevel.Error));
    }

    [Fact]
    public async Task Migrating_a_database_that_exists_without_the_schema_logs_no_error()
    {
        var identityDatabase = NewDatabase();
        await database.ExecuteAsync("postgres", $"CREATE DATABASE {identityDatabase}");
        var logs = new RecordingLoggerProvider();
        using var host = database.Host(identityDatabase, logs: logs);

        await host.Services.MigrateStrataraIdentityStoreAsync(TestContext.Current.CancellationToken);

        AssertNoErrorWhileMigrating(logs);
        Assert.NotEmpty(await database.ReadAsync(identityDatabase, History));
    }

    [Fact]
    public async Task Migrating_a_database_that_does_not_exist_logs_no_error()
    {
        var identityDatabase = NewDatabase();
        var logs = new RecordingLoggerProvider();
        using var host = database.Host(identityDatabase, logs: logs);

        await host.Services.MigrateStrataraIdentityStoreAsync(TestContext.Current.CancellationToken);

        AssertNoErrorWhileMigrating(logs);
        Assert.NotEmpty(await database.ReadAsync(identityDatabase, History));
    }

    [Fact]
    public async Task Several_hosts_migrating_a_database_that_does_not_exist_at_once_log_no_error()
    {
        var identityDatabase = NewDatabase();
        var logs = Enumerable.Range(0, 4).Select(_ => new RecordingLoggerProvider()).ToList();
        var hosts = logs.Select(log => database.Host(identityDatabase, logs: log)).ToList();
        try
        {
            await Task.WhenAll(hosts.Select(host =>
                host.Services.MigrateStrataraIdentityStoreAsync(TestContext.Current.CancellationToken)));
        }
        finally
        {
            hosts.ForEach(host => host.Dispose());
        }

        Assert.All(logs, log => Assert.Empty(log.AtLeast(LogLevel.Error)));
        var applied = await database.ReadAsync(identityDatabase, History);
        Assert.NotEmpty(applied);
        Assert.Equal(applied.Distinct(), applied);
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

        var applied = await database.ReadAsync(identityDatabase, History);
        var logs = new RecordingLoggerProvider();
        using var second = database.Host(identityDatabase, logs: logs);

        await second.Services.MigrateStrataraIdentityStoreAsync(TestContext.Current.CancellationToken);

        AssertNoErrorWhileMigrating(logs);
        Assert.Equal(applied, await database.ReadAsync(identityDatabase, History));
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> _entries = new();

        public IReadOnlyList<string> AtLeast(LogLevel level) =>
            _entries.Where(e => e.Level >= level).Select(e => $"{e.Level} {e.Category}: {e.Message}").ToList();

        public IReadOnlyList<string> InCategory(string category) =>
            _entries.Where(e => e.Category == category).Select(e => e.Message).ToList();

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
