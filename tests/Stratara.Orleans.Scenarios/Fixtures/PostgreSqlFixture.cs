using Npgsql;
using Testcontainers.PostgreSql;

namespace Stratara.Orleans.IntegrationTests.Fixtures;

/// <summary>
/// One PostgreSQL container per test collection. Each context type gets a database of its own on
/// it: <c>EnsureCreated</c> only builds a schema into an empty database, so two contexts with
/// different models must not share one. The connection limit is raised because a run of every
/// test class — the analysis workflow's, or a local one — holds more connections at once than
/// PostgreSQL's default of 100 allows. The pools close a connection after seconds idle rather than
/// Npgsql's five minutes: every test class leaves pools behind for databases it no longer uses, and
/// kept for five minutes those alone reach the limit, so that a later test fails for want of a
/// connection. A connection still held by the code under test is not idle to its pool and stays
/// counted.
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
        ConnectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            ConnectionIdleLifetime = 5,
            ConnectionPruningInterval = 1,
        }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
