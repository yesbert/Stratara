using StackExchange.Redis;
using Testcontainers.Redis;

namespace Stratara.Outbox.RabbitMQ.IntegrationTests.Fixtures;

/// <summary>
/// xUnit collection fixture that boots a single Redis container per test collection
/// and exposes a shared <see cref="IConnectionMultiplexer"/>. Individual tests flush
/// the keyspace before they run so they do not leak state across each other.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7-alpine").Build();

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        var options = ConfigurationOptions.Parse(_container.GetConnectionString());
        options.AllowAdmin = true;
        Connection = await ConnectionMultiplexer.ConnectAsync(options);
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.CloseAsync();
        Connection.Dispose();
        await _container.DisposeAsync();
    }

    public async Task FlushAsync()
    {
        foreach (var endpoint in Connection.GetEndPoints())
        {
            await Connection.GetServer(endpoint).FlushDatabaseAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = nameof(RedisCollection);
}
