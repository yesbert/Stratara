using System.Globalization;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Timers;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>
/// Task 7.2: two silos of one cluster, one of them killed. Singleton work running on the killed silo is taken over
/// by the surviving one, and a timer registered on the killed silo fires exactly once in the cluster. The surviving
/// silo votes the dead one out after its missed probes, so both cases wait up to three minutes.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class TwoSiloKillTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private static readonly TimeSpan TakeoverTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TimerDueIn = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Singleton_work_on_a_killed_silo_is_taken_over_by_the_surviving_silo()
    {
        var clusterId = $"stratara-poc-takeover-{Guid.NewGuid():N}";
        var store = postgres.ConnectionStringFor("poc_singleton_takeover");
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);

        await using var first = await PocHostProcess.StartAsync("singleton", Environment(store, clusterId, siloPort: 11291, gatewayPort: 30181));
        await using var second = await PocHostProcess.StartAsync("singleton", Environment(store, clusterId, siloPort: 11292, gatewayPort: 30182));
        var hosts = new Dictionary<int, PocHostProcess> { [11291] = first, [11292] = second };

        Assert.True(await WaitForAsync(() => second.SendAsync("last-silo"), reply => reply != "none", TimeSpan.FromSeconds(60)), "the singleton work never ran");
        var running = int.Parse(await second.SendAsync("last-silo"), CultureInfo.InvariantCulture);
        var surviving = running == 11291 ? 11292 : 11291;
        Assert.Equal("0", await hosts[surviving].SendAsync($"runs {surviving}"));

        var killedAt = DateTimeOffset.UtcNow;
        hosts[running].Kill();

        Assert.True(
            await WaitForAsync(() => hosts[surviving].SendAsync($"runs-since {surviving} {killedAt.ToUnixTimeMilliseconds()}"), reply => reply != "0", TakeoverTimeout),
            $"the surviving silo did not take the work over. Log:{System.Environment.NewLine}{string.Join(System.Environment.NewLine, hosts[surviving].Log.TakeLast(60))}");
    }

    [Fact]
    public async Task A_timer_registered_on_a_killed_silo_fires_once_in_the_cluster()
    {
        var clusterId = $"stratara-poc-timer-{Guid.NewGuid():N}";
        var store = postgres.ConnectionStringFor("poc_two_silo_timers");
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        var owner = $"two-silo-{Guid.NewGuid():N}";

        await using var registering = await PocHostProcess.StartAsync("timers", Environment(store, clusterId, siloPort: 11293, gatewayPort: 30183));
        await using var surviving = await PocHostProcess.StartAsync("timers", Environment(store, clusterId, siloPort: 11294, gatewayPort: 30184));

        var dueAt = DateTimeOffset.UtcNow + TimerDueIn;
        Assert.Equal("ok", await registering.SendAsync($"add-owner {owner}"));
        Assert.Equal("ok", await registering.SendAsync($"register-at {owner} expire {dueAt.ToUnixTimeMilliseconds()}"));
        Assert.Equal("1", await surviving.SendAsync($"timers {owner}"));
        Assert.Equal("0", await surviving.SendAsync($"firings {owner}"));
        Assert.True(DateTimeOffset.UtcNow < dueAt, "the timer came due before the kill, so its firing would prove nothing");

        registering.Kill();

        Assert.True(
            await WaitForAsync(() => surviving.SendAsync($"firings {owner}"), reply => reply == "1", TakeoverTimeout),
            $"the timer did not fire in the cluster after its silo was killed. Log:{System.Environment.NewLine}{string.Join(System.Environment.NewLine, surviving.Log.TakeLast(60))}");
        await Task.Delay(PocSilo.RefreshReminderListPeriod * 2);
        Assert.Equal("1", await surviving.SendAsync($"firings {owner}"));
        Assert.Equal("0", await surviving.SendAsync($"timers {owner}"));
    }

    private Dictionary<string, string> Environment(string store, string clusterId, int siloPort, int gatewayPort) =>
        PocHostSettings.ToEnvironment(store, postgres.ConnectionStringFor("poc_orleans"), redis.ConnectionString, rabbit: string.Empty, siloPort, gatewayPort, clusterId: clusterId);

    private static async Task<bool> WaitForAsync(Func<Task<string>> read, Func<string, bool> ready, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ready(await read()))
            {
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }
}
