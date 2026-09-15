using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Timers;

namespace Stratara.Orleans.IntegrationTests.Sagas;

/// <summary>
/// A stateful process schedules a timeout and the host is killed before it is due: after the restart the
/// timeout still fires and the process's own stream records it — three kills, each after the timer is known to
/// exist. Task 5.2: a kill between the step's timer registration and its append leaves the timer and no recorded
/// step; the fact is delivered again after the restart, and the timeout fires once.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class SagaProcessTimeoutTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int Kills = 3;
    private const int ExpiryTimeoutMs = 30_000;
    private static readonly TimeSpan TimerTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_process_timeout_survives_a_kill_and_fires_after_the_restart()
    {
        var environment = await EnvironmentForAsync("poc_saga_process_store", "poc_saga_process_read", siloPort: 11221, gatewayPort: 30110);

        for (var kill = 0; kill < Kills; kill++)
        {
            var streamId = Guid.NewGuid();

            var host = await PocHostProcess.StartAsync("saga", environment);
            Assert.Equal("ok", await host.SendAsync($"start {streamId}"));
            Assert.True(await WaitForAsync(host, $"timers {streamId}", "1", TimerTimeout), Failure($"Kill {kill + 1}: the process's timer did not exist before the kill", host));
            host.Kill();

            await using var restarted = await PocHostProcess.StartAsync("saga", environment);
            var expired = await restarted.SendAsync($"expired {streamId} {ExpiryTimeoutMs}");

            Assert.True(expired == "true", Failure($"Kill {kill + 1}: the process did not expire after the restart (reply '{expired}')", restarted));
        }
    }

    [Fact]
    public async Task A_kill_between_the_timer_registration_and_the_append_fires_the_timeout_once_after_the_restart()
    {
        var environment = await EnvironmentForAsync("poc_saga_process_hold_store", "poc_saga_process_hold_read", siloPort: 11222, gatewayPort: 30111);
        var streamId = Guid.NewGuid();

        var host = await PocHostProcess.StartAsync("saga", environment);
        Assert.Equal("ok", await host.SendAsync("hold-registrations"));
        Assert.Equal("ok", await host.SendAsync($"start {streamId}"));
        Assert.True(await WaitForAsync(host, $"timers {streamId}", "1", TimerTimeout), Failure("the held step did not register its timer", host));
        Assert.Equal("no-process", await host.SendAsync($"expired {streamId} 0"));
        host.Kill();

        await using var restarted = await PocHostProcess.StartAsync("saga", environment);
        var expired = await restarted.SendAsync($"expired {streamId} {ExpiryTimeoutMs}");
        Assert.True(expired == "true", Failure($"the process did not expire after the restart (reply '{expired}')", restarted));

        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.Equal("1", await restarted.SendAsync($"expirations {streamId}"));
        Assert.Equal("0", await restarted.SendAsync($"timers {streamId}"));
    }

    private async Task<Dictionary<string, string>> EnvironmentForAsync(string storeDatabase, string readDatabase, int siloPort, int gatewayPort)
    {
        var store = postgres.ConnectionStringFor(storeDatabase);
        var read = postgres.ConnectionStringFor(readDatabase);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(read);
        return PocHostSettings.ToEnvironment(
            store,
            postgres.ConnectionStringFor("poc_orleans"),
            redis.ConnectionString,
            rabbit.ConnectionString,
            siloPort,
            gatewayPort,
            read);
    }

    private static string Failure(string what, PocHostProcess host) =>
        $"{what}. Log:{Environment.NewLine}{string.Join(Environment.NewLine, host.Log.TakeLast(60))}";

    private static async Task<bool> WaitForAsync(PocHostProcess host, string command, string expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await host.SendAsync(command) == expected)
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }
}
