using Microsoft.Extensions.Configuration;
using Testcontainers.RabbitMq;

namespace Stratara.Outbox.RabbitMQ.IntegrationTests.Fixtures;

/// <summary>
/// xUnit collection fixture that boots a single RabbitMQ container per test collection
/// and exposes an <see cref="IConfiguration"/> populated with the broker's connection
/// string for use with <c>Stratara.Outbox.RabbitMQ.Messaging.RabbitMqBus</c>.
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();

    /// <summary>Connection string to the running RabbitMQ instance (set after <see cref="InitializeAsync"/>).</summary>
    public string ConnectionString { get; private set; } = null!;

    /// <summary>
    /// Configuration containing the resolved <c>ConnectionStrings:rabbitmq</c> value so
    /// <c>RabbitMqBus</c>'s constructor picks the URI-based factory path.
    /// </summary>
    public IConfiguration Configuration { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        Configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:rabbitmq"] = ConnectionString,
            })
            .Build();
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class RabbitMqCollection : ICollectionFixture<RabbitMqFixture>
{
    public const string Name = nameof(RabbitMqCollection);
}
