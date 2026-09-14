using System.Diagnostics;
using Npgsql;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Stratara.Orleans.Benchmarks;

/// <summary>
/// R1 of the expectations: a single silo killed and restarted on the same endpoint. The first
/// attempt (see the aborted run) showed the join itself takes about a second and a half — the
/// silo marks its older clone dead on start — so what the archived T5 waited for is the reminder
/// service, not the membership. Per restart this run therefore registers a durable timer due in
/// three seconds, kills the host, restarts it, and measures the time from the restart until the
/// host reports ready and until that timer has fired; under the test profile T5 ran with, the
/// production profile, and the production profile with the shortened membership settings.
/// </summary>
public static class RestartDelayRun
{
    private const int DueInMs = 3_000;
    private static readonly TimeSpan FireTimeout = TimeSpan.FromMinutes(5);

    private static readonly (PocSiloProfile Profile, PocSiloMembership Membership)[] Settings =
    [
        (PocSiloProfile.Test, PocSiloMembership.Default),
        (PocSiloProfile.Production, PocSiloMembership.Default),
        (PocSiloProfile.Production, PocSiloMembership.ShortIAmAlive),
    ];

    public static async Task<int> RunAsync(string evidenceRoot, int restarts)
    {
        var run = Evidence.CreateRunDirectory(evidenceRoot, "restart-delay");
        await using var postgres = new PostgreSqlBuilder(PostgreSqlFixture.Image).WithCommand("-c", "max_connections=400").Build();
        await using var redis = new RedisBuilder(RedisFixture.Image).Build();
        await using var rabbit = new RabbitMqBuilder(RabbitMqFixture.Image).Build();
        await Task.WhenAll(postgres.StartAsync(), redis.StartAsync(), rabbit.StartAsync());
        Evidence.WriteEnvironment(run,
            new Dictionary<string, string> { ["postgres"] = PostgreSqlFixture.Image, ["redis"] = RedisFixture.Image, ["rabbitmq"] = RabbitMqFixture.Image },
            new { restarts, dueInMs = DueInMs, settings = Settings.Select(s => $"{s.Profile}/{s.Membership}").ToArray() });

        var results = new List<object>();
        var port = 0;
        foreach (var (profile, membership) in Settings)
        {
            port++;
            var label = $"{profile}/{membership}";
            var store = Database(postgres.GetConnectionString(), $"r1_{port}");
            var orleans = Database(postgres.GetConnectionString(), $"r1_orleans_{port}");
            await EnsureDatabaseAsync(store);
            var environment = PocHostSettings.ToEnvironment(
                store, orleans, redis.GetConnectionString(), rabbit.GetConnectionString(), 11600 + port, 30500 + port,
                profile: profile, membership: membership);

            var coldStarted = Stopwatch.GetTimestamp();
            var host = await PocHostProcess.StartAsync("timers", environment);
            var coldReady = Stopwatch.GetElapsedTime(coldStarted).TotalSeconds;
            Console.WriteLine($"{label,-28} cold start: ready {coldReady:F1} s");
            results.Add(new { profile = profile.ToString(), membership = membership.ToString(), shape = "same-endpoint", restart = 0, readySeconds = (double?)coldReady, timerFiredSeconds = (double?)null });

            for (var restart = 1; restart <= restarts; restart++)
            {
                var owner = $"owner-{port}-{restart}-{Guid.NewGuid():N}";
                await host.SendAsync($"add-owner {owner}");
                await host.SendAsync($"register {owner} expire {DueInMs}");
                host.Kill();
                await host.DisposeAsync();

                var started = Stopwatch.GetTimestamp();
                host = await PocHostProcess.StartAsync("timers", environment);
                var ready = Stopwatch.GetElapsedTime(started).TotalSeconds;
                var fired = await WaitForFiringAsync(host, owner, started);
                Console.WriteLine($"{label,-28} restart {restart}: ready {ready:F1} s, timer fired after {fired:F1} s");
                results.Add(new { profile = profile.ToString(), membership = membership.ToString(), shape = "same-endpoint", restart, readySeconds = (double?)ready, timerFiredSeconds = double.IsNaN(fired) ? null : (double?)fired });
            }

            await host.SendExpectingExitAsync("exit", TimeSpan.FromMinutes(1));
            await host.DisposeAsync();
        }

        // The second shape, found by the integration suite: a silo that joins on another endpoint
        // while a killed silo's entry is still Active in the membership table. It has to reach that
        // silo before it may join, and waits for the entry to age out.
        foreach (var membership in new[] { PocSiloMembership.Default, PocSiloMembership.ShortIAmAlive })
        {
            port++;
            var label = $"join-after-kill/{membership}";
            var store = Database(postgres.GetConnectionString(), $"r1_{port}");
            var orleans = Database(postgres.GetConnectionString(), $"r1_orleans_{port}");
            await EnsureDatabaseAsync(store);

            for (var iteration = 1; iteration <= restarts; iteration++)
            {
                var killedPort = 11700 + port * 10 + iteration;
                var killed = await PocHostProcess.StartAsync("timers", PocHostSettings.ToEnvironment(
                    store, orleans, redis.GetConnectionString(), rabbit.GetConnectionString(), killedPort, killedPort + 20_000,
                    profile: PocSiloProfile.Production, membership: membership));
                killed.Kill();
                await killed.DisposeAsync();

                var joiningPort = killedPort + 5;
                var started = Stopwatch.GetTimestamp();
                double? ready;
                PocHostProcess? joining = null;
                try
                {
                    joining = await PocHostProcess.StartAsync("timers", PocHostSettings.ToEnvironment(
                        store, orleans, redis.GetConnectionString(), rabbit.GetConnectionString(), joiningPort, joiningPort + 20_000,
                        profile: PocSiloProfile.Production, membership: membership));
                    ready = Stopwatch.GetElapsedTime(started).TotalSeconds;
                    Console.WriteLine($"{label,-28} join {iteration}: ready {ready:F1} s");
                }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
                {
                    ready = null;
                    Console.WriteLine($"{label,-28} join {iteration}: not ready within the host's two-minute start timeout ({Stopwatch.GetElapsedTime(started).TotalSeconds:F0} s)");
                }

                results.Add(new { profile = PocSiloProfile.Production.ToString(), membership = membership.ToString(), shape = "join-after-kill", restart = iteration, readySeconds = ready, timerFiredSeconds = (double?)null });
                if (joining is not null)
                {
                    await joining.SendExpectingExitAsync("exit", TimeSpan.FromMinutes(1));
                    await joining.DisposeAsync();
                }
            }
        }

        Evidence.WriteResult(run, new { measurement = "restart-delay", results });
        Console.WriteLine($"raw result: {run}");
        return 0;
    }

    private static async Task<double> WaitForFiringAsync(PocHostProcess host, string owner, long started)
    {
        var deadline = DateTimeOffset.UtcNow + FireTimeout;
        while (await host.SendAsync($"firings {owner}") != "1")
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return double.NaN;
            }

            await Task.Delay(100);
        }

        return Stopwatch.GetElapsedTime(started).TotalSeconds;
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
