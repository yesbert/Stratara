using StackExchange.Redis;
using Testcontainers.Redis;

namespace Stratara.Orleans.IntegrationTests.Fixtures;

/// <summary>
/// One Redis container per test collection, with an admin connection so a test can flush the
/// keyspace and inspect what the grain directory left behind.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    public const string Image = "redis:7-alpine";

    private readonly RedisContainer _container = new RedisBuilder(Image).Build();

    public string ConnectionString { get; private set; } = null!;

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        var options = ConfigurationOptions.Parse(ConnectionString);
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

    public int CountKeys()
    {
        var count = 0;
        foreach (var endpoint in Connection.GetEndPoints())
        {
            count += Connection.GetServer(endpoint).Keys().Count();
        }

        return count;
    }
}
