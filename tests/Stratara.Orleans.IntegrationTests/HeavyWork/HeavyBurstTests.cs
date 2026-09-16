using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;
using Stratara.Orleans.Aggregates;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.Singleton;
using Npgsql;

namespace Stratara.Orleans.IntegrationTests.HeavyWork;

/// <summary>
/// T6 of the expectations: interactive commands measured alone, then while 500 heavy units of
/// 200 ms drain through the bounded worker pool. Interactive p99 stays within twice its baseline and
/// within baseline plus 100 ms; heavy work never exceeds the cluster-wide limit. The burst queues units
/// for far longer than the two-second grace the test configures, and the drain runs beside it: every unit
/// runs exactly once, because a heavy hand-over is leased from its acceptance, not from its first slot
/// (scenario <em>A heavy burst queues commands longer than the grace</em>).
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class HeavyBurstTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private const int InteractiveCommands = 200;
    private const int HeavyUnits = 500;
    private const int HeavyDelayMs = 200;
    private const int ClusterWideLimit = 4;
    private const string BurstStore = "poc_heavy_store";
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BurstDrainTimeout = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task Interactive_latency_stays_within_its_range_under_a_heavy_burst()
    {
        using var app = await StartAsync(siloPort: 11231, gatewayPort: 30120);

        var baseline = await MeasureInteractiveAsync(app.Services);

        var units = Enumerable.Range(0, HeavyUnits).Select(_ => Guid.NewGuid()).ToList();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(NewSession());
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
            foreach (var unit in units)
            {
                await dispatcher.EnqueueCommandAsync(new CountedHeavyProbe(unit, HeavyDelayMs));
            }
        }

        var permits = app.Services.GetRequiredService<IGrainFactory>().GetGrain<IHeavyWorkPermitGrain>(0);
        var maxInUse = 0;
        var sampling = SampleAsync(permits, value => maxInUse = Math.Max(maxInUse, value), TimeSpan.FromSeconds(6));
        var underBurst = await MeasureInteractiveAsync(app.Services);
        await sampling;

        var baselineP99 = Percentile(baseline, 0.99);
        var burstP99 = Percentile(underBurst, 0.99);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"interactive p50/p99 alone {Percentile(baseline, 0.5):F1}/{baselineP99:F1} ms, under burst {Percentile(underBurst, 0.5):F1}/{burstP99:F1} ms; heavy in use at most {maxInUse} of {ClusterWideLimit}");
        WriteEvidence(baseline, underBurst, maxInUse);

        Assert.True(maxInUse <= ClusterWideLimit, $"heavy work ran {maxInUse} wide, above the limit of {ClusterWideLimit}");
        Assert.True(burstP99 <= Math.Max(2 * baselineP99, baselineP99 + 100), $"interactive p99 under burst {burstP99:F1} ms is outside its range (baseline {baselineP99:F1} ms)");

        var store = postgres.ConnectionStringFor(BurstStore);
        Assert.True(await WaitUntilAsync(async () => await OutboxCountAsync(store) == 0, BurstDrainTimeout), $"the burst did not drain: {await OutboxCountAsync(store)} records left");
        await Task.Delay(Grace * 2);
        var executions = app.Services.GetRequiredService<ExecutionCount>();
        var runTwice = units.Where(unit => executions.Started(unit) != 1).ToList();
        Assert.True(runTwice.Count == 0, $"{runTwice.Count} of {HeavyUnits} units did not run exactly once; counts: {string.Join(", ", runTwice.Take(5).Select(unit => executions.Started(unit)))}");
        await app.StopAsync();
    }

    private static async Task<long> OutboxCountAsync(string store)
    {
        await using var connection = new NpgsqlConnection(store);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM outbox_entry", connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>
    /// Task 3.3: a worker silo is killed while its units hold every permit. The permit grain lives on the
    /// surviving silo, so its record of the permits survives the kill; the permits come back once their
    /// lease has lapsed or the cluster has declared the worker dead, and the bound is whole again.
    /// </summary>
    [Fact]
    public async Task Permits_held_by_a_killed_worker_silo_are_released_and_the_bound_is_whole_again()
    {
        const string clusterId = "stratara-poc-heavy-lease";
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        using var survivor = await StartPermitHolderAsync(orleansConnectionString, clusterId, siloPort: 11232, gatewayPort: 30121);
        var permits = survivor.Services.GetRequiredService<IGrainFactory>().GetGrain<IHeavyWorkPermitGrain>(0);
        Assert.Equal(0, await permits.InUseAsync());

        var store = postgres.ConnectionStringFor("poc_heavy_lease_store");
        await Timers.PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        var environment = PocHostSettings.ToEnvironment(store, orleansConnectionString, redis.ConnectionString, rabbit.ConnectionString, siloPort: 11233, gatewayPort: 30122, clusterId: clusterId);
        await using var worker = await PocHostProcess.StartAsync("heavy", environment);
        Assert.Equal("ok", await worker.SendAsync("enqueue-heavy 6 120000"));

        Assert.True(
            await WaitUntilAsync(async () => await permits.InUseAsync() == HeavyScenario.ClusterWideLimit, TimeSpan.FromSeconds(30)),
            $"the worker did not take every permit; in use {await permits.InUseAsync()}. Log:{Environment.NewLine}{string.Join(Environment.NewLine, worker.Log.TakeLast(40))}");

        worker.Kill();

        Assert.True(
            await WaitUntilAsync(async () => await permits.InUseAsync() == 0, HeavyScenario.PermitLease * 4),
            $"the killed worker's permits were not released; in use {await permits.InUseAsync()}");

        var silo = survivor.Services.GetRequiredService<global::Orleans.Runtime.ILocalSiloDetails>().SiloAddress;
        var units = Enumerable.Range(0, HeavyScenario.ClusterWideLimit).Select(_ => Guid.NewGuid()).ToList();
        foreach (var unit in units)
        {
            Assert.True(await permits.TryAcquireAsync(unit, silo), "the bound was not whole again");
        }

        Assert.False(await permits.TryAcquireAsync(Guid.NewGuid(), silo), "the bound admitted more than its limit");
        foreach (var unit in units)
        {
            await permits.ReleaseAsync(unit);
        }

        await survivor.StopAsync();
    }

    private async Task<IHost> StartPermitHolderAsync(string orleansConnectionString, string clusterId, int siloPort, int gatewayPort)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort, clusterId: clusterId));
        builder.Services.ConfigureStrataraHeavyWork(options =>
        {
            options.ClusterWideLimit = HeavyScenario.ClusterWideLimit;
            options.PermitLease = HeavyScenario.PermitLease;
        });

        var app = builder.Build();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(250);
        }

        return false;
    }

    private static async Task<List<double>> MeasureInteractiveAsync(IServiceProvider services)
    {
        var latencies = new List<double>(InteractiveCommands);
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(NewSession());
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        for (var i = 0; i < InteractiveCommands; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await mediator.HandleAsync(new InteractiveProbe(Guid.NewGuid()));
            latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        return latencies;
    }

    private static async Task SampleAsync(IHeavyWorkPermitGrain permits, Action<int> observe, TimeSpan duration)
    {
        var deadline = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < deadline)
        {
            observe(await permits.InUseAsync());
            await Task.Delay(50);
        }
    }

    private static double Percentile(List<double> values, double percentile)
    {
        var sorted = values.Order().ToList();
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private async Task<IHost> StartAsync(int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor(BurstStore),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.AddBackendServices();
        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddScoped<ICommandHandler<InteractiveProbe>, InteractiveProbeHandler>()
            .AddSingleton<ExecutionCount>()
            .AddScoped<ICommandHandler<CountedHeavyProbe>, CountedHeavyProbeHandler>()
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<InteractiveProbe>()
            .AddTrustedType<CountedHeavyProbe>()
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher(options => options.IntentGrace = Grace)
            .AddStrataraIntentStore<PocWriteDbContext>()
            .ConfigureStrataraHeavyWork(options => options.ClusterWideLimit = ClusterWideLimit)
            .AddStrataraSingletonWork<OutboxDrainWork>(options => options.KeepAlivePeriod = TimeSpan.FromSeconds(5))
            .Configure<OutboxDrainOptions>(options => options.PollingInterval = TimeSpan.FromMilliseconds(500));

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static SessionContext NewSession()
    {
        var tenantId = Guid.NewGuid();
        return new SessionContext(Guid.CreateVersion7().ToString("N"), null, null, tenantId, tenantId, tenantId, null);
    }

    private static void WriteEvidence(List<double> baseline, List<double> underBurst, int maxInUse)
    {
        var directory = Environment.GetEnvironmentVariable("POC_EVIDENCE_DIR");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        var run = Path.Combine(directory, "heavy-burst", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(run);
        var result = new
        {
            interactiveCommands = InteractiveCommands,
            heavyUnits = HeavyUnits,
            heavyDelayMs = HeavyDelayMs,
            clusterWideLimit = ClusterWideLimit,
            maxHeavyInUse = maxInUse,
            baselineMs = baseline,
            underBurstMs = underBurst,
        };
        File.WriteAllText(Path.Combine(run, "result.json"), JsonSerializer.Serialize(result, IndentedJson));
    }
}

/// <summary>A heavy unit whose executions are counted per unit.</summary>
public sealed record CountedHeavyProbe(Guid AggregateId, int DelayMs) : ICommand, IAggregateScopedCommand, IHeavyCommand;

public sealed class CountedHeavyProbeHandler(ExecutionCount executions) : ICommandHandler<CountedHeavyProbe>
{
    public async Task HandleAsync(CountedHeavyProbe command, CancellationToken cancellationToken)
    {
        executions.MarkStarted(command.AggregateId);
        await Task.Delay(command.DelayMs, cancellationToken);
        executions.MarkCompleted(command.AggregateId);
    }
}
