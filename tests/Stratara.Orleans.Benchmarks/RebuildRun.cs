using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Session;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.Projections;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Stratara.Orleans.Benchmarks;

/// <summary>
/// B4 of the expectations: a store of many events over three projections; today's full replay —
/// every read model truncated, every event re-applied to every projection — against the rebuild of
/// one projection while the other two keep applying live events. Duration of each, and how many live
/// events the untouched projections applied while it ran.
/// </summary>
public static class RebuildRun
{
    private const string Rebuilt = nameof(CounterViewProjection);
    private const int Streams = 20_000;
    private const int EventsPerStream = 5;

    public static async Task<int> RunAsync(string evidenceRoot, int events, int liveRatePerSecond)
    {
        var run = Evidence.CreateRunDirectory(evidenceRoot, "rebuild");
        await using var postgres = new PostgreSqlBuilder(PostgreSqlFixture.Image).WithCommand("-c", "max_connections=400").Build();
        await using var redis = new RedisBuilder(RedisFixture.Image).Build();
        await using var rabbit = new RabbitMqBuilder(RabbitMqFixture.Image).Build();
        await Task.WhenAll(postgres.StartAsync(), redis.StartAsync(), rabbit.StartAsync());
        Evidence.WriteEnvironment(run,
            new Dictionary<string, string> { ["postgres"] = PostgreSqlFixture.Image, ["redis"] = RedisFixture.Image, ["rabbitmq"] = RabbitMqFixture.Image },
            new { events, liveRatePerSecond, projections = 3, profile = PocSiloProfile.Production.ToString() });

        var results = new List<object>();

        var busStore = PostgreSqlFixture.ConnectionStringFor(postgres.GetConnectionString(), "b4_bus");
        var busRead = PostgreSqlFixture.ConnectionStringFor(postgres.GetConnectionString(), "b4_bus_read");
        await PocSilo.EnsureDatabaseAsync(busStore);
        await PocSilo.EnsureDatabaseAsync(busRead);
        results.Add(await FullReplayAsync(busStore, busRead, rabbit.GetConnectionString(), events, liveRatePerSecond));

        var grainStore = PostgreSqlFixture.ConnectionStringFor(postgres.GetConnectionString(), "b4_grain");
        var grainRead = PostgreSqlFixture.ConnectionStringFor(postgres.GetConnectionString(), "b4_grain_read");
        await PocSilo.EnsureDatabaseAsync(grainStore);
        await PocSilo.EnsureDatabaseAsync(grainRead);
        results.Add(await PerProjectionRebuildAsync(grainStore, grainRead, PostgreSqlFixture.ConnectionStringFor(postgres.GetConnectionString(), "b4_orleans"), redis.GetConnectionString(), rabbit.GetConnectionString(), events, liveRatePerSecond));

        Evidence.WriteResult(run, new { measurement = "rebuild", results });
        Console.WriteLine($"raw result: {run}");
        return 0;
    }

    private static async Task<object> FullReplayAsync(string store, string read, string rabbit, int events, int liveRate)
    {
        var settings = new PocHostSettings(store, read, string.Empty, string.Empty, rabbit, 0, 0, PocSiloProfile.Production);
        using var host = await new ProjectionScenario(ProjectionPath.Bus).BuildAsync(settings);
        await host.StartAsync();

        var (seeded, _) = await SeedAsync(host.Services, events);
        await WaitForAuditRowsAsync(host.Services, seeded, TimeSpan.FromMinutes(10));

        var replayState = host.Services.GetRequiredService<IProjectionReplayState>();
        var started = Stopwatch.GetTimestamp();
        replayState.RequestReplay();
        await Task.Delay(500);
        var live = LiveAppendsAsync(host.Services, liveRate, () => !replayState.IsReplayActive);
        while (replayState.IsReplayActive)
        {
            await Task.Delay(250);
        }

        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var liveAppended = await live;
        var liveLatencies = await LiveLatenciesAsync(host.Services, liveAppended);
        await host.StopAsync();

        Report("full replay (bus)", seconds, seeded, liveAppended.Count, liveLatencies);
        return new { path = "full-replay", events = seeded, seconds, liveAppended = liveAppended.Count, liveLatenciesMs = liveLatencies };
    }

    private static async Task<object> PerProjectionRebuildAsync(string store, string read, string orleans, string redis, string rabbit, int events, int liveRate)
    {
        var settings = new PocHostSettings(store, read, orleans, redis, rabbit, 11501, 30401, PocSiloProfile.Production);
        using var host = await new ProjectionScenario(ProjectionPath.Grain).BuildAsync(settings);
        await host.StartAsync();

        var (seeded, streams) = await SeedAsync(host.Services, events);
        await WaitForAuditRowsAsync(host.Services, seeded, TimeSpan.FromMinutes(10));
        await WaitForViewsAsync(host.Services, streams, TimeSpan.FromMinutes(10));

        var started = Stopwatch.GetTimestamp();
        var rebuilding = true;
        var live = LiveAppendsAsync(host.Services, liveRate, () => !rebuilding);
        await host.Services.GetRequiredService<IProjectionRebuilder>().RebuildAsync(Rebuilt);
        await WaitForViewsAsync(host.Services, streams, TimeSpan.FromMinutes(10));
        rebuilding = false;

        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var liveAppended = await live;
        var liveLatencies = await LiveLatenciesAsync(host.Services, liveAppended);
        await host.StopAsync();

        Report("per-projection rebuild (grain)", seconds, seeded, liveAppended.Count, liveLatencies);
        return new { path = "per-projection-rebuild", events = seeded, seconds, liveAppended = liveAppended.Count, liveLatenciesMs = liveLatencies };
    }

    private static void Report(string path, double seconds, int seeded, int liveCount, List<double> liveLatencies)
    {
        var sorted = liveLatencies.Order().ToList();
        var p50 = sorted.Count > 0 ? sorted[sorted.Count / 2] : double.NaN;
        var p99 = sorted.Count > 0 ? sorted[Math.Clamp((int)Math.Ceiling(0.99 * sorted.Count) - 1, 0, sorted.Count - 1)] : double.NaN;
        Console.WriteLine($"{path}: {seconds:F1} s for {seeded} events; {sorted.Count} of {liveCount} live events reached the audit projection, p50 {p50:F0} ms, p99 {p99:F0} ms");
    }

    private static async Task<(int Events, int Streams)> SeedAsync(IServiceProvider services, int events)
    {
        var streams = Math.Min(Streams, Math.Max(1, events / EventsPerStream));
        var perStream = events / streams;
        var appended = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, streams), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (_, ct) =>
        {
            var streamId = Guid.NewGuid();
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
            var source = scope.ServiceProvider.GetRequiredService<IEventSource>();
            await source.CreateAsync<Counter>(streamId, new CounterCreated(streamId), ct);
            for (var i = 1; i < perStream; i++)
            {
                await source.AppendAsync<Counter>(streamId, new CounterIncremented(streamId, 1), ct);
            }

            await source.SaveChangesAsync(ct);
            Interlocked.Add(ref appended, perStream);
        });
        Console.WriteLine($"seeded {appended} events over {streams} streams");
        return (appended, streams);
    }

    private static async Task<Dictionary<Guid, DateTimeOffset>> LiveAppendsAsync(IServiceProvider services, int ratePerSecond, Func<bool> stop)
    {
        var appended = new Dictionary<Guid, DateTimeOffset>();
        var interval = TimeSpan.FromSeconds(1.0 / ratePerSecond);
        while (!stop())
        {
            var streamId = Guid.NewGuid();
            await using (var scope = services.CreateAsyncScope())
            {
                scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
                var source = scope.ServiceProvider.GetRequiredService<IEventSource>();
                await source.CreateAsync<Counter>(streamId, new CounterCreated(streamId));
                appended[streamId] = DateTimeOffset.UtcNow;
                await source.SaveChangesAsync();
            }

            await Task.Delay(interval);
        }

        return appended;
    }

    /// <summary>
    /// How long each live event took to reach the audit projection — the one that was not being
    /// rebuilt. On the bus path the replay truncates that projection too, so a live event is applied
    /// only when the replay reaches it; on the grain path the other projections never stop.
    /// </summary>
    private static async Task<List<double>> LiveLatenciesAsync(IServiceProvider services, Dictionary<Guid, DateTimeOffset> appended)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        await using var scope = services.CreateAsyncScope();
        await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
        var streams = appended.Keys.ToList();
        var applied = await read.CounterAudits.AsNoTracking()
            .Where(a => streams.Contains(a.StreamId))
            .ToDictionaryAsync(a => a.StreamId, a => a.AppliedAt);
        return appended
            .Where(pair => applied.ContainsKey(pair.Key))
            .Select(pair => (applied[pair.Key] - pair.Value).TotalMilliseconds)
            .ToList();
    }

    private static async Task WaitForAuditRowsAsync(IServiceProvider services, int expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = services.CreateAsyncScope();
            await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
            if (await read.CounterAudits.CountAsync() >= expected)
            {
                return;
            }

            await Task.Delay(1000);
        }

        Console.WriteLine($"  the audit projection did not reach {expected} rows within {timeout}");
    }

    private static async Task WaitForViewsAsync(IServiceProvider services, int expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = services.CreateAsyncScope();
            await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
            if (await read.CounterViews.CountAsync() >= expected)
            {
                return;
            }

            await Task.Delay(1000);
        }

        Console.WriteLine($"  the counter view did not reach {expected} rows within {timeout}");
    }
}
