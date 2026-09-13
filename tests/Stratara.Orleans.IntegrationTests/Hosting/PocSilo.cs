using System.Net;
using Npgsql;
using Orleans.Configuration;
using Orleans.Hosting;
using StackExchange.Redis;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>
/// The silo shape the proof of concept measures: ADO.NET clustering and reminders on PostgreSQL, the
/// Redis grain directory, and reminder periods short enough for a test to observe. One method
/// configures it so every test's silo is the same silo.
/// </summary>
public static class PocSilo
{
    public const string ClusterId = "stratara-poc";
    public const string ServiceId = "stratara-poc";
    public const string AdoNetInvariant = "Npgsql";
    public static readonly TimeSpan MinimumReminderPeriod = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan RefreshReminderListPeriod = TimeSpan.FromSeconds(5);

    private static readonly string[] Scripts = ["PostgreSQL-Main.sql", "PostgreSQL-Clustering.sql", "PostgreSQL-Reminders.sql"];

    public static ISiloBuilder Configure(ISiloBuilder silo, string orleansConnectionString, string redisConnectionString, int siloPort, int gatewayPort)
    {
        var redis = ConfigurationOptions.Parse(redisConnectionString);

        silo.Configure<ClusterOptions>(options =>
        {
            options.ClusterId = ClusterId;
            options.ServiceId = ServiceId;
        });
        silo.Configure<EndpointOptions>(options =>
        {
            options.AdvertisedIPAddress = IPAddress.Loopback;
            options.SiloPort = siloPort;
            options.GatewayPort = gatewayPort;
        });
        silo.UseAdoNetClustering(options =>
        {
            options.Invariant = AdoNetInvariant;
            options.ConnectionString = orleansConnectionString;
        });
        silo.UseAdoNetReminderService(options =>
        {
            options.Invariant = AdoNetInvariant;
            options.ConnectionString = orleansConnectionString;
        });
        silo.UseRedisGrainDirectoryAsDefault(options => options.ConfigurationOptions = redis);
        silo.Configure<ReminderOptions>(options =>
        {
            options.MinimumReminderPeriod = MinimumReminderPeriod;
            options.RefreshReminderListPeriod = RefreshReminderListPeriod;
            options.ReminderLoadingWindow = RefreshReminderListPeriod * 2;
        });

        return silo;
    }

    /// <summary>
    /// Applies the three Orleans scripts to the database once. The scripts are not idempotent, so a
    /// database that already carries the query table is left alone.
    /// </summary>
    public static async Task EnsureSchemaAsync(string orleansConnectionString)
    {
        await EnsureDatabaseAsync(orleansConnectionString);

        await using var connection = new NpgsqlConnection(orleansConnectionString);
        await connection.OpenAsync();

        await using (var probe = new NpgsqlCommand("SELECT to_regclass('orleansquery')::text", connection))
        {
            if (await probe.ExecuteScalarAsync() is string)
            {
                return;
            }
        }

        foreach (var script in Scripts)
        {
            var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "OrleansSql", script));
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task EnsureDatabaseAsync(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var database = builder.Database ?? throw new InvalidOperationException("The Orleans connection string names no database.");
        builder.Database = "postgres";

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        exists.Parameters.AddWithValue("name", database);
        if (await exists.ExecuteScalarAsync() is not null)
        {
            return;
        }

        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
        await create.ExecuteNonQueryAsync();
    }
}
