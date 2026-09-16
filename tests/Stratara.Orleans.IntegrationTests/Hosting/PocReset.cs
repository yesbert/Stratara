using Npgsql;
using StackExchange.Redis;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>
/// What the reset tests need beside the shipped reset: the directory cleanup a host supplies for the Redis
/// grain directory under test, and counts of what remains afterwards.
/// </summary>
public static class PocReset
{
    /// <summary>Removes the proof of concept's directory keys from Redis and returns how many it removed.</summary>
    public static async Task<long> ClearDirectoryAsync(string redisConnectionString)
    {
        var options = ConfigurationOptions.Parse(redisConnectionString);
        options.AllowAdmin = true;
        await using var redis = await ConnectionMultiplexer.ConnectAsync(options);
        long removed = 0;
        foreach (var endpoint in redis.GetEndPoints())
        {
            var keys = redis.GetServer(endpoint).Keys(pattern: $"*{PocSilo.ClusterId}*").ToArray();
            if (keys.Length > 0)
            {
                removed += await redis.GetDatabase().KeyDeleteAsync(keys);
            }
        }

        return removed;
    }

    public static async Task<long> CountAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>Runs the three Orleans scripts under a schema of their own, so the tables live outside the connection's default.</summary>
    public static async Task CreateRuntimeTablesInSchemaAsync(string connectionString, string schema)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var probe = new NpgsqlCommand($"SELECT to_regclass('{schema}.orleansquery')::text", connection))
        {
            if (await probe.ExecuteScalarAsync() is string)
            {
                return;
            }
        }

        await using (var createSchema = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS {schema}", connection))
        {
            await createSchema.ExecuteNonQueryAsync();
        }

        await using (var searchPath = new NpgsqlCommand($"SET search_path TO {schema}", connection))
        {
            await searchPath.ExecuteNonQueryAsync();
        }

        foreach (var script in new[] { "PostgreSQL-Main.sql", "PostgreSQL-Clustering.sql", "PostgreSQL-Reminders.sql" })
        {
            var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "OrleansSql", script));
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Inserts reminder and membership rows of one deployment straight into the tables, as a run of the silo would have.</summary>
    public static async Task SeedDeploymentAsync(string connectionString, string schema, string deployment, int reminders, int membershipRows)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var version = new NpgsqlCommand($"INSERT INTO {schema}.orleansmembershipversiontable (deploymentid) VALUES (@d) ON CONFLICT DO NOTHING", connection))
        {
            version.Parameters.AddWithValue("d", deployment);
            await version.ExecuteNonQueryAsync();
        }

        for (var i = 0; i < membershipRows; i++)
        {
            await using var row = new NpgsqlCommand(
                $"INSERT INTO {schema}.orleansmembershiptable (deploymentid, address, port, generation, siloname, hostname, status, starttime, iamalivetime) VALUES (@d, '127.0.0.1', @p, 1, 'seeded', 'localhost', 3, now(), now())",
                connection);
            row.Parameters.AddWithValue("d", deployment);
            row.Parameters.AddWithValue("p", 20000 + i);
            await row.ExecuteNonQueryAsync();
        }

        for (var i = 0; i < reminders; i++)
        {
            await using var row = new NpgsqlCommand(
                $"INSERT INTO {schema}.orleansreminderstable (serviceid, grainid, remindername, starttime, period, grainhash, version) VALUES (@s, @g, 'expire', now(), 60000, @h, 0)",
                connection);
            row.Parameters.AddWithValue("s", deployment);
            row.Parameters.AddWithValue("g", $"seeded-owner-{i}");
            row.Parameters.AddWithValue("h", i);
            await row.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Counts the rows of one deployment in one of the runtime's tables.</summary>
    public static async Task<long> CountAsync(string connectionString, string table, string column, string value)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE {column} = @value", connection);
        command.Parameters.AddWithValue("value", value);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
