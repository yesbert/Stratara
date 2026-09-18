using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Stratara.Diagnostics;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.Singleton;
using Stratara.Abstractions.Singleton;

namespace Stratara.Orleans.IntegrationTests.Singleton;

/// <summary>
/// Block 2: two silos in one cluster, both registering the same work; over a run of several periods
/// the work executes once per period, on one silo only, with no two executions closer together than
/// the period allows. A run that throws is logged under the framework's event and the work runs again; a work
/// registered with its name is first constructed when the silo becomes active.
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

    [Fact]
    public async Task A_failing_run_is_logged_with_the_framework_event_and_the_work_runs_again()
    {
        var probe = new FlakyWorkProbe();
        var logs = new CapturedLogs();
        using var app = await StartSiloAsync(11332, 30222, builder => builder.Services
            .AddSingleton(probe)
            .AddStrataraSingletonWork<FlakyWork>(options => options.KeepAlivePeriod = TimeSpan.FromSeconds(5)), logs);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (probe.Succeeded == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        await app.StopAsync();

        Assert.True(probe.Succeeded > 0, "The work did not run again after its failing run.");
        var failed = Assert.Single(logs.Entries, entry => entry.EventId == LogEvents.Orleans.SingletonWorkFailed);
        Assert.Contains(FlakyWork.WorkName, failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_work_registered_with_its_name_is_first_constructed_when_the_silo_becomes_active()
    {
        var probe = new ConstructionProbe();
        using var app = await StartSiloAsync(11333, 30223, builder => builder.Services
            .AddSingleton(probe)
            .AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(probe)
            .AddStrataraSingletonWork<LateWork>(LateWork.WorkName, options => options.KeepAlivePeriod = TimeSpan.FromSeconds(5)));

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (probe.Runs == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        await app.StopAsync();

        Assert.True(probe.Runs > 0, "The named work never ran.");
        Assert.NotEmpty(probe.Constructions);
        Assert.All(probe.Constructions, becameActive => Assert.True(becameActive, "The named work was constructed before the silo became active."));
    }

    private async Task<IHost> StartSiloAsync(int siloPort, int gatewayPort, Action<HostApplicationBuilder> register, CapturedLogs? logs = null)
    {
        var orleansConnectionString = postgres.ConnectionStringFor(OrleansDatabase);
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        register(builder);
        var app = builder.Build();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
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

    public sealed class FlakyWorkProbe
    {
        private int _attempts;
        private int _succeeded;

        public int Succeeded => _succeeded;

        public int NextAttempt() => Interlocked.Increment(ref _attempts);

        public void Succeed() => Interlocked.Increment(ref _succeeded);
    }

    /// <summary>Throws on its first run and succeeds on every later one.</summary>
    public sealed class FlakyWork(FlakyWorkProbe probe) : ISingletonWork
    {
        public const string WorkName = "flaky";

        public string Name => WorkName;

        public TimeSpan Period => SingletonWorkTests.Period;

        public Task RunAsync(CancellationToken cancellationToken)
        {
            if (probe.NextAttempt() == 1)
            {
                throw new InvalidOperationException("The first run of the flaky work fails.");
            }

            probe.Succeed();
            return Task.CompletedTask;
        }
    }

    /// <summary>Records, for every construction of the work, whether the silo had reached the stage before active.</summary>
    public sealed class ConstructionProbe : ILifecycleParticipant<ISiloLifecycle>
    {
        private volatile bool _becameActive;
        private int _runs;

        public ConcurrentQueue<bool> Constructions { get; } = new();

        public int Runs => _runs;

        public void Participate(ISiloLifecycle lifecycle) =>
            lifecycle.Subscribe(nameof(ConstructionProbe), ServiceLifecycleStage.BecomeActive, _ =>
            {
                _becameActive = true;
                return Task.CompletedTask;
            });

        public void Constructed() => Constructions.Enqueue(_becameActive);

        public void Ran() => Interlocked.Increment(ref _runs);
    }

    public sealed class LateWork : ISingletonWork
    {
        public const string WorkName = "late";

        private readonly ConstructionProbe _probe;

        public LateWork(ConstructionProbe probe)
        {
            _probe = probe;
            probe.Constructed();
        }

        public string Name => WorkName;

        public TimeSpan Period => SingletonWorkTests.Period;

        public Task RunAsync(CancellationToken cancellationToken)
        {
            _probe.Ran();
            return Task.CompletedTask;
        }
    }
}
