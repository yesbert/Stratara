using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Session;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Stratara.Orleans.Benchmarks;

/// <summary>
/// B2 of the expectations: the time from a commit returning to the read model holding the row, per
/// event, for the bus push, the checkpoint catch-up woken by a hint, and the hybrid. Events are
/// appended at a steady rate; the latency is the projection's own timestamp minus the appender's,
/// taken just before the commit, both from this machine's clock — so every path pays the same
/// commit and none can be applied before its clock started. Three repetitions per path; every
/// latency in the raw result.
/// </summary>
public static class ReadModelLatencyRun
{
    private static readonly (string Scenario, int SiloPort, int GatewayPort)[] Paths =
    [
        ("projection-bus", 11251, 30140),
        ("projection-grain", 11252, 30141),
        ("projection-hybrid", 11253, 30142),
    ];

    public static async Task<int> RunAsync(string evidenceRoot, int events, int ratePerSecond, int repetitions)
    {
        var run = Evidence.CreateRunDirectory(evidenceRoot, "read-model-latency");
        await using var postgres = new PostgreSqlBuilder(PostgreSqlFixture.Image).WithCommand("-c", "max_connections=400").Build();
        await using var redis = new RedisBuilder(RedisFixture.Image).Build();
        await using var rabbit = new RabbitMqBuilder(RabbitMqFixture.Image).Build();
        await Task.WhenAll(postgres.StartAsync(), redis.StartAsync(), rabbit.StartAsync());
        Evidence.WriteEnvironment(run,
            new Dictionary<string, string> { ["postgres"] = PostgreSqlFixture.Image, ["redis"] = RedisFixture.Image, ["rabbitmq"] = RabbitMqFixture.Image },
            new { events, ratePerSecond, repetitions, profile = PocSiloProfile.Production.ToString() });

        var results = new List<object>();
        foreach (var (scenario, siloPort, gatewayPort) in Paths)
        {
            for (var repetition = 0; repetition < repetitions; repetition++)
            {
                var store = Database(postgres.GetConnectionString(), $"b2_{scenario.Replace('-', '_')}_{repetition}");
                var read = Database(postgres.GetConnectionString(), $"b2_{scenario.Replace('-', '_')}_{repetition}_read");
                await EnsureDatabaseAsync(store);
                await EnsureDatabaseAsync(read);
                var settings = new PocHostSettings(store, read, Database(postgres.GetConnectionString(), "b2_orleans"), redis.GetConnectionString(), rabbit.GetConnectionString(), siloPort, gatewayPort, PocSiloProfile.Production, Membership: PocSiloMembership.Default);

                var latencies = await MeasureAsync(scenario, settings, events, ratePerSecond);
                var sorted = latencies.Order().ToList();
                Console.WriteLine($"{scenario,-18} rep {repetition + 1}: p50 {Percentile(sorted, 0.5),7:F1} ms  p99 {Percentile(sorted, 0.99),7:F1} ms  max {sorted[^1],7:F1} ms ({latencies.Count} events)");
                results.Add(new { scenario, repetition, events = latencies.Count, p50 = Percentile(sorted, 0.5), p99 = Percentile(sorted, 0.99), max = sorted[^1], latenciesMs = latencies });
            }
        }

        Evidence.WriteResult(run, new { measurement = "read-model-latency", results });
        Console.WriteLine($"raw result: {run}");
        return 0;
    }

    private static async Task<List<double>> MeasureAsync(string scenario, PocHostSettings settings, int events, int ratePerSecond)
    {
        var host = await PocHostEntry.Scenarios[scenario]().BuildAsync(settings);
        using (host)
        {
            await host.StartAsync();
            await Task.Delay(TimeSpan.FromSeconds(3));

            var committedAt = new Dictionary<Guid, DateTimeOffset>(events);
            var interval = TimeSpan.FromSeconds(1.0 / ratePerSecond);
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < events; i++)
            {
                var streamId = Guid.NewGuid();
                await using (var scope = host.Services.CreateAsyncScope())
                {
                    scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
                    var source = scope.ServiceProvider.GetRequiredService<IEventSource>();
                    await source.CreateAsync<Counter>(streamId, new CounterCreated(streamId));
                    // The clock starts before the commit: the save returns only after the publish or the
                    // nudge, and the push path can apply the row before that return.
                    committedAt[streamId] = DateTimeOffset.UtcNow;
                    await source.SaveChangesAsync();
                }

                var due = started + (long)((i + 1) * interval.TotalSeconds * Stopwatch.Frequency);
                var wait = TimeSpan.FromSeconds((due - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait);
                }
            }

            var views = await WaitForViewsAsync(host.Services, committedAt.Keys, TimeSpan.FromSeconds(60));
            await host.StopAsync();

            return committedAt
                .Where(pair => views.ContainsKey(pair.Key))
                .Select(pair => (views[pair.Key] - pair.Value).TotalMilliseconds)
                .ToList();
        }
    }

    private static async Task<Dictionary<Guid, DateTimeOffset>> WaitForViewsAsync(IServiceProvider services, IReadOnlyCollection<Guid> streams, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            await using var scope = services.CreateAsyncScope();
            await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
            var views = await read.CounterViews.AsNoTracking().ToDictionaryAsync(v => v.StreamId, v => v.AppliedAt);
            if (views.Count >= streams.Count || DateTimeOffset.UtcNow >= deadline)
            {
                if (views.Count < streams.Count)
                {
                    Console.WriteLine($"  {streams.Count - views.Count} of {streams.Count} events never reached the read model within {timeout}");
                }

                return views;
            }

            await Task.Delay(250);
        }
    }

    private static double Percentile(List<double> sorted, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
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
