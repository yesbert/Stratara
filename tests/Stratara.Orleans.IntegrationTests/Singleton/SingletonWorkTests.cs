using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.Singleton;
using Stratara.Abstractions.Singleton;

namespace Stratara.Orleans.IntegrationTests.Singleton;

/// <summary>
/// Block 2: two silos in one cluster, both registering the same work; over a run of several periods
/// the work executes once per period, on one silo only, with no two executions closer together than
/// the period allows.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class SingletonWorkTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private const string OrleansDatabase = "poc_orleans";
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Two_silos_run_the_work_once_per_period_on_one_of_them()
    {
        var executions = new ConcurrentQueue<(string Silo, DateTimeOffset At)>();
        using var first = await StartAsync("silo-1", executions, siloPort: 11151, gatewayPort: 30040);
        using var second = await StartAsync("silo-2", executions, siloPort: 11152, gatewayPort: 30041);

        await Task.Delay(Observation);
        await first.StopAsync();
        await second.StopAsync();

        var ordered = executions.OrderBy(e => e.At).ToList();
        var expected = (int)(Observation / Period);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{ordered.Count} executions in {Observation} at a {Period} period; silos: {string.Join(",", ordered.Select(e => e.Silo).Distinct())}");

        Assert.InRange(ordered.Count, expected - 2, expected + 1);
        Assert.Single(ordered.Select(e => e.Silo).Distinct());
        for (var i = 1; i < ordered.Count; i++)
        {
            var gap = ordered[i].At - ordered[i - 1].At;
            Assert.True(gap >= Period / 2, $"Executions {i - 1} and {i} are only {gap} apart — the work ran twice in one period.");
        }
    }

    private async Task<IHost> StartAsync(string siloName, ConcurrentQueue<(string, DateTimeOffset)> executions, int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor(OrleansDatabase);
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        // Two silos, one cluster: the test is about the work running once across them.
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort, clusterId: $"{PocSilo.ClusterId}-singleton"));
        builder.Services
            .AddSingleton(new ProbeWorkContext(siloName, executions))
            .AddStrataraSingletonWork<ProbeWork>(options => options.KeepAlivePeriod = TimeSpan.FromSeconds(5));

        var app = builder.Build();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    public sealed record ProbeWorkContext(string SiloName, ConcurrentQueue<(string Silo, DateTimeOffset At)> Executions);

    public sealed class ProbeWork(ProbeWorkContext context) : ISingletonWork
    {
        public string Name => "probe";

        public TimeSpan Period => SingletonWorkTests.Period;

        public Task RunAsync(CancellationToken cancellationToken)
        {
            context.Executions.Enqueue((context.SiloName, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }
    }
}
