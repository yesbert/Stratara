using System.Diagnostics;
using Npgsql;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Stratara.Orleans.Benchmarks;

/// <summary>
/// R1 of the expectations: a single silo killed and restarted on the same endpoint, under each
/// membership setting. Per restart: the time from starting the process until the host reports
/// ready — which is the silo having joined — and until a command dispatched through the durable
/// intent has been applied, which is the first grain call that succeeded. Every restart is in the
/// raw result.
/// </summary>
public static class RestartDelayRun
{
    private static readonly PocSiloMembership[] Settings = [PocSiloMembership.Default, PocSiloMembership.ShortIAmAlive];

    public static async Task<int> RunAsync(string evidenceRoot, int restarts)
    {
        var run = Evidence.CreateRunDirectory(evidenceRoot, "restart-delay");
        await using var postgres = new PostgreSqlBuilder(PostgreSqlFixture.Image).WithCommand("-c", "max_connections=400").Build();
        await using var redis = new RedisBuilder(RedisFixture.Image).Build();
        await using var rabbit = new RabbitMqBuilder(RabbitMqFixture.Image).Build();
        await Task.WhenAll(postgres.StartAsync(), redis.StartAsync(), rabbit.StartAsync());
        Evidence.WriteEnvironment(run,
            new Dictionary<string, string> { ["postgres"] = PostgreSqlFixture.Image, ["redis"] = RedisFixture.Image, ["rabbitmq"] = RabbitMqFixture.Image },
            new { restarts, settings = Settings.Select(s => s.ToString()).ToArray(), profile = PocSiloProfile.Production.ToString() });

        var results = new List<object>();
        var port = 0;
        foreach (var membership in Settings)
        {
            port++;
            var store = Database(postgres.GetConnectionString(), $"r1_{membership.ToString().ToLowerInvariant()}");
            var orleans = Database(postgres.GetConnectionString(), $"r1_orleans_{membership.ToString().ToLowerInvariant()}");
            await EnsureDatabaseAsync(store);
            var environment = PocHostSettings.ToEnvironment(
                store, orleans, redis.GetConnectionString(), rabbit.GetConnectionString(), 11600 + port, 30500 + port,
                profile: PocSiloProfile.Production, membership: membership);

            // The first start has no predecessor; it is recorded but is not a restart.
            var cold = await StartAndProbeAsync(environment);
            var host = cold.Host;
            Console.WriteLine($"{membership,-20} cold start: ready {cold.ReadySeconds:F1} s, first command applied {cold.AppliedSeconds:F1} s");
            results.Add(new { membership = membership.ToString(), restart = 0, readySeconds = cold.ReadySeconds, appliedSeconds = cold.AppliedSeconds });

            for (var restart = 1; restart <= restarts; restart++)
            {
                host.Kill();
                await host.DisposeAsync();
                var probe = await StartAndProbeAsync(environment);
                host = probe.Host;
                Console.WriteLine($"{membership,-20} restart {restart}: ready {probe.ReadySeconds:F1} s, first command applied {probe.AppliedSeconds:F1} s");
                results.Add(new { membership = membership.ToString(), restart, readySeconds = probe.ReadySeconds, appliedSeconds = probe.AppliedSeconds });
            }

            await host.SendAsync("exit");
            await host.DisposeAsync();
        }

        Evidence.WriteResult(run, new { measurement = "restart-delay", results });
        Console.WriteLine($"raw result: {run}");
        return 0;
    }

    private static async Task<(PocHostProcess Host, double ReadySeconds, double AppliedSeconds)> StartAndProbeAsync(IReadOnlyDictionary<string, string> environment)
    {
        var started = Stopwatch.GetTimestamp();
        var host = await PocHostProcess.StartAsync("intent", environment);
        var ready = Stopwatch.GetElapsedTime(started).TotalSeconds;

        var aggregateId = Guid.NewGuid();
        await host.SendAsync($"enqueue {aggregateId} 0");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        while (await host.SendAsync($"applied {aggregateId}") != "true")
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return (host, ready, double.NaN);
            }

            await Task.Delay(100);
        }

        return (host, ready, Stopwatch.GetElapsedTime(started).TotalSeconds);
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
