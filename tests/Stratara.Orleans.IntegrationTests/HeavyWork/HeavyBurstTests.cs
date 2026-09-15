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
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.HeavyWork;

/// <summary>
/// T6 of the expectations: interactive commands measured alone, then while 500 heavy units of
/// 200 ms drain through the bounded worker pool. Interactive p99 stays within twice its baseline and
/// within baseline plus 100 ms; heavy work never exceeds the cluster-wide limit.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class HeavyBurstTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int InteractiveCommands = 200;
    private const int HeavyUnits = 500;
    private const int HeavyDelayMs = 200;
    private const int ClusterWideLimit = 4;

    [Fact]
    public async Task Interactive_latency_stays_within_its_range_under_a_heavy_burst()
    {
        using var app = await StartAsync(siloPort: 11231, gatewayPort: 30120);

        var baseline = await MeasureInteractiveAsync(app.Services);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(NewSession());
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
            for (var i = 0; i < HeavyUnits; i++)
            {
                await dispatcher.EnqueueCommandAsync(new HeavyProbe(Guid.NewGuid(), HeavyDelayMs));
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
        await app.StopAsync();
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
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor("poc_heavy_store"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.AddBackendServices();
        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddScoped<ICommandHandler<InteractiveProbe>, InteractiveProbeHandler>()
            .AddScoped<ICommandHandler<HeavyProbe>, HeavyProbeHandler>()
            .AddAggregatesFromAssemblyContaining<HeavyBurstTests>()
            .AddTrustedType<InteractiveProbe>()
            .AddTrustedType<HeavyProbe>()
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher()
            .AddStrataraIntentStore<PocWriteDbContext>()
            .ConfigureStrataraHeavyWork(options => options.ClusterWideLimit = ClusterWideLimit);

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
        File.WriteAllText(Path.Combine(run, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed record InteractiveProbe(Guid AggregateId) : ICommand, IAggregateScopedCommand;

public sealed record HeavyProbe(Guid AggregateId, int DelayMs) : ICommand, IAggregateScopedCommand, IHeavyCommand;

public sealed class InteractiveProbeHandler : ICommandHandler<InteractiveProbe>
{
    public Task HandleAsync(InteractiveProbe command, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class HeavyProbeHandler : ICommandHandler<HeavyProbe>
{
    public Task HandleAsync(HeavyProbe command, CancellationToken cancellationToken) => Task.Delay(command.DelayMs, cancellationToken);
}
