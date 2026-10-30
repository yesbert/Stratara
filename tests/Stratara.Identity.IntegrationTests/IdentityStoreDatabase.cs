using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Stratara.EventSourcing.EntityFrameworkCore.IdentityStore;
using Testcontainers.PostgreSql;

namespace Stratara.Identity.IntegrationTests;

/// <summary>
/// One PostgreSQL container for the class. Every test names databases of its own, so a migration one test applied is
/// never another test's starting point.
/// </summary>
public sealed class IdentityStoreDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    private string _connectionString = null!;

    public string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(_connectionString) { Database = database }.ConnectionString;

    /// <summary>
    /// A host that shares the identity store: its own default database, the identity store in
    /// <paramref name="identityDatabase"/>, accounts registered the way the framework's identity registration does.
    /// </summary>
    public IHost Host(string identityDatabase, string? defaultDatabase = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = ConnectionStringFor(defaultDatabase ?? $"host_{Guid.NewGuid():N}"),
            [$"ConnectionStrings:{StrataraIdentityStore.ConnectionStringName}"] = ConnectionStringFor(identityDatabase),
        });
        builder.Services.AddLogging();
        builder.Services.AddDataProtection();
        builder.Services.AddStrataraIdentityStore();
        builder.AddAspNetIdentity<IdentityUser, StrataraIdentityStore>();
        return builder.Build();
    }

    public async Task<int> ExecuteAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionStringFor(database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<List<string>> ReadAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionStringFor(database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetValue(0).ToString()!);
        }

        return values;
    }

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
