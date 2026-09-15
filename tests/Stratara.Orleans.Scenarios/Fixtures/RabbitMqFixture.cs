using Testcontainers.RabbitMq;

namespace Stratara.Orleans.IntegrationTests.Fixtures;

/// <summary>
/// One RabbitMQ container per test collection. The bus workers under comparison subscribe to it;
/// the Orleans path never touches it.
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    public const string Image = "rabbitmq:4-management-alpine";

    private readonly RabbitMqContainer _container = new RabbitMqBuilder(Image).Build();

    public string ConnectionString { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
