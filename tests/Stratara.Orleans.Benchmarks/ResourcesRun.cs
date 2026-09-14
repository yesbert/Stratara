using System.Diagnostics;
using Npgsql;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Stratara.Orleans.Benchmarks;

/// <summary>
/// B5 of the expectations: the bus-worker host and the silo host as separate processes, sampled
/// once a second for resident memory and processor time — idle first, then while each dispatches
/// the same probe command at the same rate to itself. Every sample is in the raw result.
/// </summary>
public static class ResourcesRun
{
    private static readonly (string Scenario, int SiloPort, int GatewayPort)[] Hosts = [("bus", 11401, 30301), ("intent", 11402, 30302)];

    public static async Task<int> RunAsync(string evidenceRoot, int idleSeconds, int loadSeconds, int ratePerSecond, PocSiloDirectory directory)
    {
        var run = Evidence.CreateRunDirectory(evidenceRoot, "resources");
        await using var postgres = new PostgreSqlBuilder(PostgreSqlFixture.Image).WithCommand("-c", "max_connections=400").Build();
        await using var redis = new RedisBuilder(RedisFixture.Image).Build();
        await using var rabbit = new RabbitMqBuilder(RabbitMqFixture.Image).Build();
        await Task.WhenAll(postgres.StartAsync(), redis.StartAsync(), rabbit.StartAsync());
        Evidence.WriteEnvironment(run,
            new Dictionary<string, string> { ["postgres"] = PostgreSqlFixture.Image, ["redis"] = RedisFixture.Image, ["rabbitmq"] = RabbitMqFixture.Image },
            new { idleSeconds, loadSeconds, ratePerSecond, profile = PocSiloProfile.Production.ToString(), directory = directory.ToString() });

        var results = new List<object>();
        foreach (var (scenario, siloPort, gatewayPort) in Hosts)
        {
            var store = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = $"b5_{scenario}" }.ConnectionString;
            await EnsureDatabaseAsync(store);
            var environment = PocHostSettings.ToEnvironment(
                store,
                new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = "b5_orleans" }.ConnectionString,
                redis.GetConnectionString(),
                rabbit.GetConnectionString(),
                siloPort,
                gatewayPort,
                profile: PocSiloProfile.Production,
                directory: directory);

            await using var host = await PocHostProcess.StartAsync(scenario, environment);
            var process = Process.GetProcessById(host.ProcessId);

            var idle = await SampleAsync(process, idleSeconds);
            var cpuBeforeLoad = process.TotalProcessorTime;
            var commands = loadSeconds * ratePerSecond;
            await host.SendAsync($"load {commands} {ratePerSecond}");
            var load = await SampleAsync(process, loadSeconds);
            var applied = long.Parse(await host.SendAsync("applied-count"));
            var cpuDuringLoad = (process.TotalProcessorTime - cpuBeforeLoad).TotalSeconds;

            var result = new
            {
                scenario,
                directory = directory.ToString(),
                idleRssMb = idle.Average(s => s.RssMb),
                loadRssMb = load.Average(s => s.RssMb),
                idleCpuSecondsPerSecond = idle.Count > 1 ? (idle[^1].CpuSeconds - idle[0].CpuSeconds) / (idle.Count - 1) : 0,
                commandsDispatched = commands,
                commandsApplied = applied,
                cpuSecondsDuringLoad = cpuDuringLoad,
                cpuSecondsPerThousandCommands = applied > 0 ? cpuDuringLoad / (applied / 1000.0) : double.NaN,
                idleSamples = idle,
                loadSamples = load,
            };
            Console.WriteLine($"{scenario,-7}: idle RSS {result.idleRssMb,7:F1} MB, load RSS {result.loadRssMb,7:F1} MB, {applied} of {commands} applied, {cpuDuringLoad:F1} CPU-s under load = {result.cpuSecondsPerThousandCommands:F2} CPU-s per 1000");
            results.Add(result);
        }

        Evidence.WriteResult(run, new { measurement = "resources", results });
        Console.WriteLine($"raw result: {run}");
        return 0;
    }

    private static async Task<List<Sample>> SampleAsync(Process process, int seconds)
    {
        var samples = new List<Sample>(seconds);
        for (var i = 0; i < seconds; i++)
        {
            process.Refresh();
            samples.Add(new Sample(DateTimeOffset.UtcNow, process.WorkingSet64 / 1024.0 / 1024.0, process.TotalProcessorTime.TotalSeconds));
            await Task.Delay(1000);
        }

        return samples;
    }

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

    public sealed record Sample(DateTimeOffset At, double RssMb, double CpuSeconds);
}
