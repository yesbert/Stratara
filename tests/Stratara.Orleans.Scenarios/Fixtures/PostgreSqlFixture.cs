using Npgsql;
using Testcontainers.PostgreSql;

namespace Stratara.Orleans.IntegrationTests.Fixtures;

/// <summary>
/// One PostgreSQL container per test collection. Each context type gets a database of its own on
/// it: <c>EnsureCreated</c> only builds a schema into an empty database, so two contexts with
/// different models must not share one. The connection limit is raised because a run of every
/// test class — the analysis workflow's, or a local one — holds more connections at once than
/// PostgreSQL's default of 100 allows.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    public const string Image = "postgres:17-alpine";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).WithCommand("-c", "max_connections=400").Build();

    public string ConnectionString { get; private set; } = null!;

    public string ConnectionStringFor(string database) => ConnectionStringFor(ConnectionString, database);

    /// <summary>The connection string with the database name replaced — for a run that started its own container.</summary>
    public static string ConnectionStringFor(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;

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
