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
/// names none gets the framework's default; and the size in effect is logged once per connection string.
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
        var resolved = NpgsqlDbContextServiceCollectionExtensions.EnsureMaxPoolSize($"{Base};{pool}", loggers: null);

        Assert.Equal(expected, new NpgsqlConnectionStringBuilder(resolved).MaxPoolSize);
    }

    [Fact]
    public void A_connection_string_that_names_no_pool_size_gets_the_default()
    {
        var resolved = NpgsqlDbContextServiceCollectionExtensions.EnsureMaxPoolSize(Base, loggers: null);

        Assert.Equal(NpgsqlDbContextServiceCollectionExtensions.DefaultMaxPoolSize, new NpgsqlConnectionStringBuilder(resolved).MaxPoolSize);
    }

    [Fact]
    public void The_size_in_effect_is_logged_once_per_connection_string_with_where_it_came_from()
    {
        var logs = new Recording();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var named = $"Host=db.example;Database=named-{Guid.NewGuid():N};Maximum Pool Size=100";
        var unnamed = $"Host=db.example;Database=unnamed-{Guid.NewGuid():N}";

        NpgsqlDbContextServiceCollectionExtensions.EnsureMaxPoolSize(named, loggers);
        NpgsqlDbContextServiceCollectionExtensions.EnsureMaxPoolSize(named, loggers);
        NpgsqlDbContextServiceCollectionExtensions.EnsureMaxPoolSize(unnamed, loggers);

        var entries = logs.Entries.Where(entry => entry.EventId == LogEvents.EventStore.PoolSizeInEffect).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Message.Contains("at most 100, from the connection string", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Message.Contains($"at most {NpgsqlDbContextServiceCollectionExtensions.DefaultMaxPoolSize}, from the framework's default", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.Message.Contains("secret", StringComparison.Ordinal));
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
        private readonly List<(int EventId, string Message)> _entries = [];

        public IReadOnlyList<(int EventId, string Message)> Entries { get { lock (_entries) { return [.. _entries]; } } }

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose()
        {
        }

        private sealed class Capture(Recording owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._entries)
                {
                    owner._entries.Add((eventId.Id, formatter(state, exception)));
                }
            }
        }
    }
}
