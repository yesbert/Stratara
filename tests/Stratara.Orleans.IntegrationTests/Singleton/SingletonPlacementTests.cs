using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Singleton;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;

namespace Stratara.Orleans.IntegrationTests.Singleton;

/// <summary>
/// Task 4.7: two silos in one cluster, only one of which registers the singleton work. The silo that registers
/// it starts eight works; without placement filtering some of them would be placed on the other silo, fail to
/// activate there and fail the start. Every work starts, and every run is on the silo that registered it.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class SingletonPlacementTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private const string OrleansDatabase = "poc_orleans";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Work_registered_on_one_silo_runs_only_there_and_neither_silo_fails()
    {
        var runs = new ConcurrentQueue<(string Silo, string Work)>();
        using var without = await StartAsync("without", runs, registerWork: false, siloPort: 11261, gatewayPort: 30151);
        using var with = await StartAsync("with", runs, registerWork: true, siloPort: 11262, gatewayPort: 30152);

        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline && runs.Select(run => run.Work).Distinct().Count() < PlacementProbes.Count)
        {
            await Task.Delay(200);
        }

        Assert.Equal(PlacementProbes.Count, runs.Select(run => run.Work).Distinct().Count());
        Assert.All(runs, run => Assert.Equal("with", run.Silo));

        await with.StopAsync();
        await without.StopAsync();
    }

    private async Task<IHost> StartAsync(string siloName, ConcurrentQueue<(string, string)> runs, bool registerWork, int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor(OrleansDatabase);
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort, clusterId: $"{PocSilo.ClusterId}-placement"));
        builder.Services.AddSingleton(new PlacementRunLog(siloName, runs));
        if (registerWork)
        {
            PlacementProbes.Register(builder.Services);
        }

        var app = builder.Build();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }
}

public sealed record PlacementRunLog(string SiloName, ConcurrentQueue<(string Silo, string Work)> Runs);

/// <summary>Eight works with distinct names, so that placement is decided eight times.</summary>
public static class PlacementProbes
{
    public const int Count = 8;

    public static void Register(IServiceCollection services)
    {
        var keepAlive = TimeSpan.FromSeconds(5);
        services
            .AddStrataraSingletonWork<PlacementProbe<Probe1>>(o => o.KeepAlivePeriod = keepAlive)
            .AddStrataraSingletonWork<PlacementProbe<Probe2>>()
            .AddStrataraSingletonWork<PlacementProbe<Probe3>>()
            .AddStrataraSingletonWork<PlacementProbe<Probe4>>()
            .AddStrataraSingletonWork<PlacementProbe<Probe5>>()
            .AddStrataraSingletonWork<PlacementProbe<Probe6>>()
            .AddStrataraSingletonWork<PlacementProbe<Probe7>>()
            .AddStrataraSingletonWork<PlacementProbe<Probe8>>();
    }

    public sealed class Probe1;

    public sealed class Probe2;

    public sealed class Probe3;

    public sealed class Probe4;

    public sealed class Probe5;

    public sealed class Probe6;

    public sealed class Probe7;

    public sealed class Probe8;
}

public sealed class PlacementProbe<TMarker>(PlacementRunLog log) : ISingletonWork
{
    public string Name => "placement-" + typeof(TMarker).Name;

    public TimeSpan Period => TimeSpan.FromMilliseconds(500);

    public Task RunAsync(CancellationToken cancellationToken)
    {
        log.Runs.Enqueue((log.SiloName, Name));
        return Task.CompletedTask;
    }
}
