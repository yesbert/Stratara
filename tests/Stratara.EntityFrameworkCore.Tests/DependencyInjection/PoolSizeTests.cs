using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Npgsql;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Diagnostics;
using Stratara.Testing;

namespace Stratara.EventSourcing.EntityFrameworkCore.Tests.DependencyInjection;

/// <summary>
/// Scenarios of <em>The connection pool size is the consumer's to choose</em>: a maximum pool size the connection
/// string names is kept, under any of Npgsql's keywords and whatever its value, 100 included; a connection string that
/// names none gets the framework's default; and the size in effect is logged once per host and database, under the
/// framework's own category, never with the password, and only counted as reported once it was written.
/// </summary>
public sealed class PoolSizeTests
{
    private const string Base = "Host=db.example;Database=store;Username=app;Password=secret";

    [Theory]
    [InlineData("Maximum Pool Size=100", 100)]
    [InlineData("MaxPoolSize=100", 100)]
    [InlineData("maximum pool size=100", 100)]
    [InlineData("Maximum Pool Size=40", 40)]
    [InlineData("Maximum Pool Size=1", 1)]
    [InlineData("Maximum Pool Size=2", 2)]
    public void A_pool_size_the_connection_string_names_is_kept(string pool, int expected)
    {
        var resolved = NpgsqlDbContextServiceCollectionExtensions.EnsureMaxPoolSize($"{Base};{pool}", report: null, loggers: null);

        Assert.Equal(expected, new NpgsqlConnectionStringBuilder(resolved).MaxPoolSize);
    }

    [Fact]
    public void A_connection_string_that_names_no_pool_size_gets_the_default()
    {
        var resolved = NpgsqlDbContextServiceCollectionExtensions.EnsureMaxPoolSize(Base, report: null, loggers: null);

        Assert.Equal(NpgsqlDbContextServiceCollectionExtensions.DefaultMaxPoolSize, new NpgsqlConnectionStringBuilder(resolved).MaxPoolSize);
    }

    [Fact]
    public void The_size_in_effect_is_logged_once_per_host_and_database_with_where_it_came_from_and_never_the_password()
    {
        var logs = new Recording();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var report = new PoolSizeReport();
        var named = $"{Base};Maximum Pool Size=100";
        var unnamed = Base.Replace("Database=store", "Database=other", StringComparison.Ordinal);

        NpgsqlDbContextServiceCollectionExtensions.EnsureMaxPoolSize(named, report, loggers);
        NpgsqlDbContextServiceCollectionExtensions.EnsureMaxPoolSize(named.Replace("Password=secret", "Password=rotated", StringComparison.Ordinal), report, loggers);
        NpgsqlDbContextServiceCollectionExtensions.EnsureMaxPoolSize(unnamed, report, loggers);

        var entries = logs.Entries.Where(entry => entry.EventId == LogEvents.EventStore.PoolSizeInEffect).ToList();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(NpgsqlDbContextServiceCollectionExtensions.PoolLogCategory, entry.Category));
        Assert.Contains(entries, entry => entry.Message.Contains("store on db.example use a pool of at most 100, from the connection string", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Message.Contains($"other on db.example use a pool of at most {NpgsqlDbContextServiceCollectionExtensions.DefaultMaxPoolSize}, from the framework's default", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.Message.Contains("secret", StringComparison.Ordinal) || entry.Message.Contains("rotated", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_host_reports_for_itself()
    {
        var logs = new Recording();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs));

        NpgsqlDbContextServiceCollectionExtensions.EnsureMaxPoolSize(Base, new PoolSizeReport(), loggers);
        NpgsqlDbContextServiceCollectionExtensions.EnsureMaxPoolSize(Base, new PoolSizeReport(), loggers);

        Assert.Equal(2, logs.Entries.Count(entry => entry.EventId == LogEvents.EventStore.PoolSizeInEffect));
    }

    [Fact]
    public void A_line_a_log_level_suppressed_is_written_once_the_level_lets_it_through()
    {
        var logs = new Recording { Enabled = false };
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var report = new PoolSizeReport();

        NpgsqlDbContextServiceCollectionExtensions.EnsureMaxPoolSize(Base, report, loggers);
        logs.Enabled = true;
        NpgsqlDbContextServiceCollectionExtensions.EnsureMaxPoolSize(Base, report, loggers);

        Assert.Single(logs.Entries, entry => entry.EventId == LogEvents.EventStore.PoolSizeInEffect);
    }

    [Fact]
    public void A_registered_context_connects_with_the_pool_size_its_connection_string_names()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:defaultdb"] = $"{Base};Maximum Pool Size=100" })
            .Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<ISessionContextProvider>(new TestSessionContextProvider(TestSessionContext.ForTenant(Guid.NewGuid())))
            .AddSingleton(Mock.Of<ISecureJsonSerializer>());
        services.AddNpgsqlWriteDbContextFactory<NpgsqlDbContextServiceCollectionExtensionsTests.TestWriteDbContext>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NpgsqlDbContextServiceCollectionExtensionsTests.TestWriteDbContext>>().CreateDbContext();

        Assert.Equal(100, new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString()).MaxPoolSize);
    }

    private sealed class Recording : ILoggerProvider
    {
        private readonly List<(int EventId, string Message, string Category)> _entries = [];

        public volatile bool Enabled = true;

        public IReadOnlyList<(int EventId, string Message, string Category)> Entries { get { lock (_entries) { return [.. _entries]; } } }

        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Capture(Recording owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => owner.Enabled;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._entries)
                {
                    owner._entries.Add((eventId.Id, formatter(state, exception), category));
                }
            }
        }
    }
}
