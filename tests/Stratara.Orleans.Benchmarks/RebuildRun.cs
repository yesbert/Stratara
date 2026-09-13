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
        await using var postgres = new PostgreSqlBuilder(PostgreSqlFixture.Image).Build();
        await using var redis = new RedisBuilder(RedisFixture.Image).Build();
        await using var rabbit = new RabbitMqBuilder(RabbitMqFixture.Image).Build();
        await Task.WhenAll(postgres.StartAsync(), redis.StartAsync(), rabbit.StartAsync());
        Evidence.WriteEnvironment(run,
            new Dictionary<string, string> { ["postgres"] = PostgreSqlFixture.Image, ["redis"] = RedisFixture.Image, ["rabbitmq"] = RabbitMqFixture.Image },
            new { events, liveRatePerSecond, projections = 3 });

        var results = new List<object>();

        var busStore = Database(postgres.GetConnectionString(), "b4_bus");
        var busRead = Database(postgres.GetConnectionString(), "b4_bus_read");
        await EnsureDatabaseAsync(busStore);
        await EnsureDatabaseAsync(busRead);
        results.Add(await FullReplayAsync(busStore, busRead, rabbit.GetConnectionString(), events, liveRatePerSecond));

        var grainStore = Database(postgres.GetConnectionString(), "b4_grain");
        var grainRead = Database(postgres.GetConnectionString(), "b4_grain_read");
        await EnsureDatabaseAsync(grainStore);
        await EnsureDatabaseAsync(grainRead);
        results.Add(await PerProjectionRebuildAsync(grainStore, grainRead, Database(postgres.GetConnectionString(), "b4_orleans"), redis.GetConnectionString(), rabbit.GetConnectionString(), events, liveRatePerSecond));

        Evidence.WriteResult(run, new { measurement = "rebuild", results });
        Console.WriteLine($"raw result: {run}");
        return 0;
    }

    private static async Task<object> FullReplayAsync(string store, string read, string rabbit, int events, int liveRate)
    {
        var settings = new PocHostSettings(store, read, string.Empty, string.Empty, rabbit, 0, 0);
        using var host = await new ProjectionScenario(ProjectionPath.Bus).BuildAsync(settings);
        host.Services.GetRequiredService<IProjectionViewTruncator>();
        await host.StartAsync();

        var seeded = await SeedAsync(host.Services, events);
        await WaitForAuditRowsAsync(host.Services, seeded, TimeSpan.FromMinutes(10));

        var replayState = host.Services.GetRequiredService<IProjectionReplayState>();
        var started = Stopwatch.GetTimestamp();
        var startedAt = DateTimeOffset.UtcNow;
        replayState.RequestReplay();
        await Task.Delay(500);
        var live = LiveAppendsAsync(host.Services, liveRate, () => !replayState.IsReplayActive);
        while (replayState.IsReplayActive)
        {
            await Task.Delay(250);
        }

        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var endedAt = DateTimeOffset.UtcNow;
        var liveAppended = await live;
        var liveAppliedDuring = await AuditRowsAppliedBetweenAsync(host.Services, startedAt, endedAt, liveAppended);
        await host.StopAsync();

        Console.WriteLine($"full replay (bus): {seconds:F1} s for {seeded} events over 3 projections; live events applied by the others during it: {liveAppliedDuring} of {liveAppended.Count}");
        return new { path = "full-replay", events = seeded, seconds, liveAppended = liveAppended.Count, liveAppliedDuring };
    }

    private static async Task<object> PerProjectionRebuildAsync(string store, string read, string orleans, string redis, string rabbit, int events, int liveRate)
    {
        var settings = new PocHostSettings(store, read, orleans, redis, rabbit, 11501, 30401);
        using var host = await new ProjectionScenario(ProjectionPath.Grain).BuildAsync(settings);
        await host.StartAsync();

        var seeded = await SeedAsync(host.Services, events);
        await WaitForAuditRowsAsync(host.Services, seeded, TimeSpan.FromMinutes(10));
        await WaitForViewsAsync(host.Services, Streams, TimeSpan.FromMinutes(10));

        var started = Stopwatch.GetTimestamp();
        var startedAt = DateTimeOffset.UtcNow;
        var rebuilding = true;
        var live = LiveAppendsAsync(host.Services, liveRate, () => !rebuilding);
        await host.Services.GetRequiredService<IProjectionRebuilder>().RebuildAsync(Rebuilt);
        await WaitForViewsAsync(host.Services, Streams, TimeSpan.FromMinutes(10));
        rebuilding = false;

        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var endedAt = DateTimeOffset.UtcNow;
        var liveAppended = await live;
        var liveAppliedDuring = await AuditRowsAppliedBetweenAsync(host.Services, startedAt, endedAt, liveAppended);
        await host.StopAsync();

        Console.WriteLine($"per-projection rebuild (grain): {seconds:F1} s for {seeded} events, one projection; live events applied by the others during it: {liveAppliedDuring} of {liveAppended.Count}");
        return new { path = "per-projection-rebuild", events = seeded, seconds, liveAppended = liveAppended.Count, liveAppliedDuring };
    }

    private static async Task<int> SeedAsync(IServiceProvider services, int events)
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
        return appended;
    }

    private static async Task<List<Guid>> LiveAppendsAsync(IServiceProvider services, int ratePerSecond, Func<bool> stop)
    {
        var appended = new List<Guid>();
        var interval = TimeSpan.FromSeconds(1.0 / ratePerSecond);
        while (!stop())
        {
            var streamId = Guid.NewGuid();
            await using (var scope = services.CreateAsyncScope())
            {
                scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
                var source = scope.ServiceProvider.GetRequiredService<IEventSource>();
                await source.CreateAsync<Counter>(streamId, new CounterCreated(streamId));
                await source.SaveChangesAsync();
            }

            appended.Add(streamId);
            await Task.Delay(interval);
        }

        return appended;
    }

    private static async Task<int> AuditRowsAppliedBetweenAsync(IServiceProvider services, DateTimeOffset from, DateTimeOffset to, List<Guid> streams)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        await using var scope = services.CreateAsyncScope();
        await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
        return await read.CounterAudits.AsNoTracking().CountAsync(a => streams.Contains(a.StreamId) && a.AppliedAt >= from && a.AppliedAt <= to);
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
