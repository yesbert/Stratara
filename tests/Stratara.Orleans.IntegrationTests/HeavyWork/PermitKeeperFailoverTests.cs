using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Orleans.Aggregates;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;

namespace Stratara.Orleans.IntegrationTests.HeavyWork;

/// <summary>
/// The silo keeping the heavy-work permits is killed while the units of another silo hold every permit, and two more
/// heavy commands are dispatched as soon as the keeper activated again elsewhere answers. That keeper starts with an
/// empty table; the bound holds all the same: no more units run than the bound at any moment, the running units are
/// counted again within a lease of the keeper answering, and the further units start only after the grace and after a
/// running unit has ended (scenario <em>The silo keeping the
/// permits dies while the bound is full</em>). The takeover waits for the cluster to vote the killed silo out.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class PermitKeeperFailoverTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int KeeperSiloPort = 11234;
    private const int WorkerSiloPort = 11235;
    private const int ObserverSiloPort = 11236;
    private const int Further = 2;
    private const int UnitMs = 120_000;
    private static readonly TimeSpan TakeoverTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Sampling = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task The_bound_holds_when_the_silo_keeping_the_permits_is_killed()
    {
        var clusterId = $"stratara-poc-keeper-{Guid.NewGuid():N}";
        var orleans = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleans);
        var store = postgres.ConnectionStringFor("poc_heavy_keeper_store");
        await PocSilo.EnsureDatabaseAsync(store);

        await using var keeper = await PocHostProcess.StartAsync("permit-keeper", Environment(store, orleans, clusterId, KeeperSiloPort, gatewayPort: 30123));
        Assert.Equal("0", await keeper.SendAsync("in-use"));

        await using var worker = await PocHostProcess.StartAsync("heavy", Environment(store, orleans, clusterId, WorkerSiloPort, gatewayPort: 30124));
        using var observer = await StartObserverAsync(orleans, clusterId);
        var grains = observer.Services.GetRequiredService<IGrainFactory>();
        var permits = grains.GetGrain<IHeavyWorkPermitGrain>(0);
        var keptOn = await grains.GetGrain<IManagementGrain>(0).GetActivationAddress(permits);
        Assert.Equal(KeeperSiloPort, keptOn?.Endpoint.Port);

        Assert.Equal("ok", await worker.SendAsync($"enqueue-heavy {HeavyScenario.ClusterWideLimit} {UnitMs}"));
        Assert.True(
            await WaitUntilAsync(async () => (await RunsAsync(worker)).Running == HeavyScenario.ClusterWideLimit && await permits.InUseAsync() == HeavyScenario.ClusterWideLimit, TimeSpan.FromSeconds(60)),
            $"the worker did not fill the bound; runs {await worker.SendAsync("runs")}. Log:{System.Environment.NewLine}{string.Join(System.Environment.NewLine, worker.Log.TakeLast(40))}");

        keeper.Kill();

        using var stop = new CancellationTokenSource();
        var keeperSide = new KeeperSide();
        var observing = keeperSide.ObserveAsync(permits, stop.Token);
        var maxRunning = 0;
        int? runningWhenBack = null;
        Runs? firstExtraStart = null;
        var deadline = DateTimeOffset.UtcNow + TakeoverTimeout + TimeSpan.FromMilliseconds(UnitMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var back = keeperSide.Back;
            var runs = await RunsAsync(worker);
            maxRunning = Math.Max(maxRunning, runs.Running);
            if (back is not null && runningWhenBack is null)
            {
                runningWhenBack = runs.Running;
                Assert.Equal("ok", await worker.SendAsync($"enqueue-heavy {Further} {UnitMs}"));
            }

            if (runs.Started > HeavyScenario.ClusterWideLimit)
            {
                firstExtraStart ??= runs;
            }

            if (runs.Started == HeavyScenario.ClusterWideLimit + Further)
            {
                break;
            }

            await Task.Delay(Sampling);
        }

        await stop.CancelAsync();
        await observing;
        var keeperBack = keeperSide.Back;
        var countedAgain = keeperSide.CountedAgain;
        var final = await RunsAsync(worker);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"keeper answered again {keeperBack:O} with {runningWhenBack} running, counted the running units again {countedAgain:O}, first further unit started by {firstExtraStart?.At:O} after {firstExtraStart?.Completed} ended; at most {maxRunning} running; final {final}");

        Assert.True(keeperBack is not null, "the permit keeper never answered again");
        Assert.True(runningWhenBack == HeavyScenario.ClusterWideLimit, $"the running units ended before the keeper returned ({runningWhenBack} running), so the takeover under a full bound was not observed");
        Assert.True(maxRunning <= HeavyScenario.ClusterWideLimit, $"{maxRunning} units ran at once, above the bound of {HeavyScenario.ClusterWideLimit}");
        Assert.True(countedAgain is not null && countedAgain - keeperBack <= HeavyScenario.PermitLease, $"the running units were not counted again within a lease of the keeper's return ({countedAgain:O} after {keeperBack:O})");
        Assert.True(firstExtraStart is not null, $"the further units never started; runs {final}. Log:{System.Environment.NewLine}{string.Join(System.Environment.NewLine, worker.Log.TakeLast(60))}");
        Assert.True(firstExtraStart.Completed >= 1, $"a further unit started before any running unit had ended: {firstExtraStart}");

        // The grace is what keeps the bound whole here, and that is what the two assertions above measure: no
        // moment above the bound, and the running units counted again within a lease. A further unit's start time
        // proves nothing about the grace, because the units of this test outlive it by minutes.
        Assert.Equal(HeavyScenario.ClusterWideLimit + Further, final.Started);
        Assert.Equal(1, final.MostStartsOfOneUnit);

        await observer.StopAsync();
    }

    private async Task<IHost> StartObserverAsync(string orleans, string clusterId)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleans, redis.ConnectionString, ObserverSiloPort, gatewayPort: 30125, clusterId: clusterId));
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

    private Dictionary<string, string> Environment(string store, string orleans, string clusterId, int siloPort, int gatewayPort) =>
        PocHostSettings.ToEnvironment(store, orleans, redis.ConnectionString, rabbit.ConnectionString, siloPort, gatewayPort, clusterId: clusterId);

    private static async Task<Runs> RunsAsync(PocHostProcess worker)
    {
        var parts = (await worker.SendAsync("runs")).Split(' ');
        return new Runs(
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture),
            int.Parse(parts[3], CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow);
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

    /// <summary>Asks the keeper from the observing silo until stopped: when it first answers after the kill, and when its table first counts the full bound again.</summary>
    private sealed class KeeperSide
    {
        private long _back;
        private long _countedAgain;

        public DateTimeOffset? Back => Read(ref _back);

        public DateTimeOffset? CountedAgain => Read(ref _countedAgain);

        public async Task ObserveAsync(IHeavyWorkPermitGrain permits, CancellationToken stop)
        {
            while (!stop.IsCancellationRequested && CountedAgain is null)
            {
                try
                {
                    var inUse = await permits.InUseAsync();
                    var now = DateTimeOffset.UtcNow.UtcTicks;
                    Interlocked.CompareExchange(ref _back, now, 0);
                    if (inUse == HeavyScenario.ClusterWideLimit && now > Interlocked.Read(ref _back))
                    {
                        Interlocked.CompareExchange(ref _countedAgain, now, 0);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _ = ex;
                }

                await Task.Delay(Sampling, CancellationToken.None);
            }
        }

        private static DateTimeOffset? Read(ref long ticks)
        {
            var value = Interlocked.Read(ref ticks);
            return value == 0 ? null : new DateTimeOffset(value, TimeSpan.Zero);
        }
    }

    private sealed record Runs(int Running, int Started, int Completed, int MostStartsOfOneUnit, DateTimeOffset At);
}
