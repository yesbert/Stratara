using System.Diagnostics;
using Npgsql;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Stratara.Orleans.Benchmarks;

/// <summary>
/// R3, found while running the suite: how long a graceful stop of each host shape takes. A stop
/// that outlives the harness's thirty-second patience — or an orchestrator's — ends in a kill, and
/// a killed silo's entry stays Active in the membership table until a later joiner has waited it
/// out. Per shape: start the host, let it settle, send <c>exit</c>, measure until the process has
/// ended; the host's last log lines are kept where the stop was slow.
/// </summary>
public static class StopDelayRun
{
    private static readonly string[] Scenarios = ["timers", "intent", "saga", "projection-grain"];
    private static readonly TimeSpan StopTimeout = TimeSpan.FromMinutes(3);

    public static async Task<int> RunAsync(string evidenceRoot, PocSiloProfile profile)
    {
        var run = Evidence.CreateRunDirectory(evidenceRoot, "stop-delay");
        await using var postgres = new PostgreSqlBuilder(PostgreSqlFixture.Image).WithCommand("-c", "max_connections=400").Build();
        await using var redis = new RedisBuilder(RedisFixture.Image).Build();
        await using var rabbit = new RabbitMqBuilder(RabbitMqFixture.Image).Build();
        await Task.WhenAll(postgres.StartAsync(), redis.StartAsync(), rabbit.StartAsync());
        Evidence.WriteEnvironment(run,
            new Dictionary<string, string> { ["postgres"] = PostgreSqlFixture.Image, ["redis"] = RedisFixture.Image, ["rabbitmq"] = RabbitMqFixture.Image },
            new { profile = profile.ToString(), scenarios = Scenarios });

        var results = new List<object>();
        var port = 0;
        foreach (var scenario in Scenarios)
        {
            port++;
            var store = Database(postgres.GetConnectionString(), $"r3_{port}");
            var read = Database(postgres.GetConnectionString(), $"r3_{port}_read");
            var orleans = Database(postgres.GetConnectionString(), $"r3_orleans_{port}");
            await EnsureDatabaseAsync(store);
            await EnsureDatabaseAsync(read);
            var environment = PocHostSettings.ToEnvironment(
                store, orleans, redis.GetConnectionString(), rabbit.GetConnectionString(), 11800 + port, 31800 + port,
                read: read, profile: profile, membership: PocSiloMembership.Default);

            for (var iteration = 1; iteration <= 1; iteration++)
            {
                var host = await PocHostProcess.StartAsync(scenario, environment);
                await Task.Delay(TimeSpan.FromSeconds(5));
                var started = Stopwatch.GetTimestamp();
                string outcome;
                double? seconds;
                try
                {
                    await host.SendExpectingExitAsync("exit", StopTimeout);
                    seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                    outcome = "exited";
                }
                catch (TimeoutException)
                {
                    seconds = null;
                    outcome = $"still running after {StopTimeout.TotalSeconds:F0} s, killed";
                    host.Kill();
                }

                var tail = host.Log.TakeLast(25).ToList();
                await host.DisposeAsync();
                Console.WriteLine($"{scenario,-16} stop {iteration}: {outcome}{(seconds is { } s ? $" after {s:F1} s" : string.Empty)}");
                results.Add(new { scenario, iteration, outcome, seconds, logTail = tail });
            }
        }

        Evidence.WriteResult(run, new { measurement = "stop-delay", results });
        Console.WriteLine($"raw result: {run}");
        return 0;
    }

    private static string Database(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;

    private static async Task EnsureDatabaseAsync(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var database = builder.Database!;
        builder.Database = "postgres";
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        exists.Parameters.AddWithValue("name", database);
        if (await exists.ExecuteScalarAsync() is null)
        {
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            await create.ExecuteNonQueryAsync();
        }
    }
}
