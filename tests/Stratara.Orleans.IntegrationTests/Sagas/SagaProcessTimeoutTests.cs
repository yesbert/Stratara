using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Timers;

namespace Stratara.Orleans.IntegrationTests.Sagas;

/// <summary>
/// Task 9.1, second half: a stateful process schedules a timeout, the host is killed before it is
/// due, and after the restart the timeout still fires and the process's own stream records it.
/// Three kills.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class SagaProcessTimeoutTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int Kills = 3;
    private const int ExpiryTimeoutMs = 30_000;

    [Fact]
    public async Task A_process_timeout_survives_a_kill_and_fires_after_the_restart()
    {
        var store = postgres.ConnectionStringFor("poc_saga_process_store");
        var read = postgres.ConnectionStringFor("poc_saga_process_read");
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(read);
        var environment = PocHostSettings.ToEnvironment(
            store,
            postgres.ConnectionStringFor("poc_orleans"),
            redis.ConnectionString,
            rabbit.ConnectionString,
            siloPort: 11221,
            gatewayPort: 30110,
            read);

        for (var kill = 0; kill < Kills; kill++)
        {
            var streamId = Guid.NewGuid();

            var host = await PocHostProcess.StartAsync("saga", environment);
            Assert.Equal("ok", await host.SendAsync($"start {streamId}"));
            await Task.Delay(TimeSpan.FromSeconds(1));
            host.Kill();

            await using var restarted = await PocHostProcess.StartAsync("saga", environment);
            var expired = await restarted.SendAsync($"expired {streamId} {ExpiryTimeoutMs}");

            Assert.True(expired == "true", $"Kill {kill + 1}: the process did not expire after the restart (reply '{expired}'). Log:{Environment.NewLine}{string.Join(Environment.NewLine, restarted.Log.TakeLast(60))}");
        }
    }
}
