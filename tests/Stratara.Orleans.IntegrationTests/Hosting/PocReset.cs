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
