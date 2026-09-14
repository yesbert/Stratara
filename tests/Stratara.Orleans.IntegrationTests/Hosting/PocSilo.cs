using System.Net;
using Npgsql;
using Orleans.Configuration;
using Orleans.Hosting;
using StackExchange.Redis;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>Which reminder and refresh settings the silo runs.</summary>
public enum PocSiloProfile
{
    /// <summary>A one-second minimum reminder period and a five-second refresh, so a test can observe a reminder.</summary>
    Test,

    /// <summary>Orleans' defaults, which is what a deployed silo pays and what a benchmark compares against the bus host.</summary>
    Production,
}

/// <summary>How the membership protocol treats a predecessor that died on the same endpoint.</summary>
public enum PocSiloMembership
{
    /// <summary>Orleans' defaults: a stale entry is skipped after three missed <c>IAmAlive</c> periods of thirty seconds.</summary>
    Default,

    /// <summary>A five-second <c>IAmAlive</c> period and two missed periods, so a stale entry is skipped after ten seconds.</summary>
    ShortIAmAlive,
}

/// <summary>Which grain directory the grains that select none — aggregate and runner grains — take.</summary>
public enum PocSiloDirectory
{
    /// <summary>Redis for every grain, as the proof of concept measured on 2026-09-13.</summary>
    RedisAsDefault,

    /// <summary>The built-in directory for grains that select none; Redis only for the grains that name the durable directory.</summary>
    BuiltInDefault,
}

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

    public static ISiloBuilder Configure(
        ISiloBuilder silo,
        string orleansConnectionString,
        string redisConnectionString,
        int siloPort,
        int gatewayPort,
        PocSiloProfile profile = PocSiloProfile.Test,
        PocSiloDirectory directory = PocSiloDirectory.RedisAsDefault,
        PocSiloMembership membership = PocSiloMembership.Default)
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
        silo.AddRedisGrainDirectory(GrainDirectories.Durable, options => options.ConfigurationOptions = redis);
        if (directory == PocSiloDirectory.RedisAsDefault)
        {
            silo.UseRedisGrainDirectoryAsDefault(options => options.ConfigurationOptions = redis);
        }

        switch (membership)
        {
            case PocSiloMembership.ShortIAmAlive:
                silo.Configure<ClusterMembershipOptions>(options =>
                {
                    options.IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(5);
                    options.NumMissedTableIAmAliveLimit = 2;
                });
                break;
            case PocSiloMembership.Default:
            default:
                break;
        }

        if (profile == PocSiloProfile.Test)
        {
            silo.Configure<ReminderOptions>(options =>
            {
                options.MinimumReminderPeriod = MinimumReminderPeriod;
                options.RefreshReminderListPeriod = RefreshReminderListPeriod;
                options.ReminderLoadingWindow = RefreshReminderListPeriod * 2;
            });
        }

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
