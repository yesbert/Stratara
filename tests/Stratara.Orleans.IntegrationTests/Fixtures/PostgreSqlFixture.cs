using Testcontainers.PostgreSql;

namespace Stratara.Orleans.IntegrationTests.Fixtures;

/// <summary>
/// One PostgreSQL container per test collection. Tests that need an empty store create their own
/// database on it, so they never see each other's rows.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    public const string Image = "postgres:17-alpine";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

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
