using Npgsql;
using Testcontainers.PostgreSql;

namespace Stratara.Orleans.IntegrationTests.Fixtures;

/// <summary>
/// One PostgreSQL container per test collection. Each context type gets a database of its own on
/// it: <c>EnsureCreated</c> only builds a schema into an empty database, so two contexts with
/// different models must not share one.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    public const string Image = "postgres:17-alpine";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    public string ConnectionString { get; private set; } = null!;

    public string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString;

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
