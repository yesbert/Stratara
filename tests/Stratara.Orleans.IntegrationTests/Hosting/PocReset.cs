using Npgsql;
using StackExchange.Redis;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>
/// Task 11.1: one reset that clears everything the Orleans path keeps outside the event stream —
/// reminders, cluster membership, the grain directory, and the projection checkpoints — so a
/// deployment can be brought back to "nothing scheduled, nothing remembered" deterministically. The
/// event stream is untouched: it is the truth, and the checkpoints will rebuild from it.
/// </summary>
/// <remarks>
/// Specific to the shape under test — ADO.NET clustering and reminders on PostgreSQL, the Redis
/// grain directory, checkpoints in a PostgreSQL read store. A shipped reset would be one
/// implementation per provider behind a port.
/// </remarks>
public static class PocReset
{
    public static async Task<ResetReport> RunAsync(string orleansConnectionString, string redisConnectionString, string? readStoreConnectionString)
    {
        var report = new ResetReport();

        await using (var connection = new NpgsqlConnection(orleansConnectionString))
        {
            await connection.OpenAsync();
            report.RemindersDeleted = await DeleteAllAsync(connection, "orleansreminderstable");
            report.MembershipRowsDeleted = await DeleteAllAsync(connection, "orleansmembershiptable");
        }

        if (readStoreConnectionString is not null)
        {
            await using var read = new NpgsqlConnection(readStoreConnectionString);
            await read.OpenAsync();
            report.CheckpointsDeleted = await DeleteAllAsync(read, "projection_checkpoint");
        }

        var options = ConfigurationOptions.Parse(redisConnectionString);
        options.AllowAdmin = true;
        await using var redis = await ConnectionMultiplexer.ConnectAsync(options);
        foreach (var endpoint in redis.GetEndPoints())
        {
            var server = redis.GetServer(endpoint);
            var keys = server.Keys(pattern: $"*{PocSilo.ClusterId}*").ToArray();
            if (keys.Length > 0)
            {
                report.DirectoryKeysDeleted += await redis.GetDatabase().KeyDeleteAsync(keys);
            }
        }

        return report;
    }

    public static async Task<long> CountAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<int> DeleteAllAsync(NpgsqlConnection connection, string table)
    {
        await using var exists = new NpgsqlCommand($"SELECT to_regclass('{table}')::text", connection);
        if (await exists.ExecuteScalarAsync() is not string)
        {
            return 0;
        }

        await using var command = new NpgsqlCommand($"DELETE FROM {table}", connection);
        return await command.ExecuteNonQueryAsync();
    }

    public sealed class ResetReport
    {
        public int RemindersDeleted { get; set; }

        public int MembershipRowsDeleted { get; set; }

        public int CheckpointsDeleted { get; set; }

        public long DirectoryKeysDeleted { get; set; }

        public override string ToString() =>
            $"reminders {RemindersDeleted}, membership rows {MembershipRowsDeleted}, checkpoints {CheckpointsDeleted}, directory keys {DirectoryKeysDeleted}";
    }
}
