using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Timers;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// Task 7.2: the host is killed after <c>EnqueueCommandAsync</c> has returned and before the
/// handler's append. After the restart the command is applied — by the drain resuming the recorded
/// intent — and the record is gone. Five kills.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class DurableIntentTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int Kills = 5;
    private const int HandlerDelayMs = 5_000;
    private static readonly TimeSpan ResumeTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task A_command_accepted_before_a_kill_is_applied_after_the_restart() =>
        RunAsync("enqueue", "poc_intent_store", siloPort: 11181, gatewayPort: 30070);

    /// <summary>Task 10.1's crash case: heavy work whose intent was recorded and whose completion never came.</summary>
    [Fact]
    public Task Heavy_work_accepted_before_a_kill_is_resumed_after_the_restart() =>
        RunAsync("enqueue-heavy", "poc_intent_heavy_store", siloPort: 11182, gatewayPort: 30071);

    private async Task RunAsync(string enqueueCommand, string database, int siloPort, int gatewayPort)
    {
        var store = postgres.ConnectionStringFor(database);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        var environment = PocHostSettings.ToEnvironment(
            store,
            postgres.ConnectionStringFor("poc_orleans"),
            redis.ConnectionString,
            rabbit.ConnectionString,
            siloPort,
            gatewayPort);

        for (var kill = 0; kill < Kills; kill++)
        {
            var aggregateId = Guid.NewGuid();

            var host = await PocHostProcess.StartAsync("intent", environment);
            var reply = await host.SendAsync($"{enqueueCommand} {aggregateId} {HandlerDelayMs}");
            Assert.True(reply.StartsWith("enqueued ", StringComparison.Ordinal), $"Kill {kill + 1}: enqueue replied '{reply}'. Log:{Environment.NewLine}{string.Join(Environment.NewLine, host.Log.TakeLast(40))}");
            Assert.Equal("false", await host.SendAsync($"applied {aggregateId}"));
            host.Kill();

            await using var restarted = await PocHostProcess.StartAsync("intent", environment);
            var applied = await WaitForAsync(restarted, $"applied {aggregateId}", "true");

            Assert.True(applied, $"Kill {kill + 1}: the command was not applied after the restart. Log:{Environment.NewLine}{string.Join(Environment.NewLine, restarted.Log)}");
            Assert.Equal("0", await restarted.SendAsync("outbox-count"));
        }
    }

    private static async Task<bool> WaitForAsync(PocHostProcess host, string command, string expected)
    {
        var deadline = DateTimeOffset.UtcNow + ResumeTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await host.SendAsync(command) == expected)
            {
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }
}
