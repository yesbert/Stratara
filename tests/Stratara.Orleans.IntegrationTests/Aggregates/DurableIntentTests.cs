using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Timers;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// The durable-intent path: a command accepted before a kill is applied after the restart — five kills per path,
/// recorded and heavy, the kill landing inside a five-second handler — a command
/// whose handler keeps failing is resumed a bounded number of times and then kept without holding back
/// the commands after it, a handler that runs longer than the grace runs once — awaiting or not yielding on the
/// aggregate path, awaiting on the heavy path — a command that names
/// its aggregate only through the interface is resumed in its aggregate's order, and a kept command an
/// operator returns is resumed with its count starting over.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class DurableIntentTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int Kills = 5;
    private const int HandlerDelayMs = 5_000;
    private const int LongHandlerDelayMs = 8_000;
    private const string Bound = "attempts=3 kept=true";
    private static readonly TimeSpan ResumeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeptTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public Task A_command_accepted_before_a_kill_is_applied_after_the_restart() =>
        RunKillsAsync("enqueue", "poc_intent_store", siloPort: 11181, gatewayPort: 30070);

    /// <summary>Task 10.1's crash case: heavy work whose intent was recorded and whose completion never came.</summary>
    [Fact]
    public Task Heavy_work_accepted_before_a_kill_is_resumed_after_the_restart() =>
        RunKillsAsync("enqueue-heavy", "poc_intent_heavy_store", siloPort: 11182, gatewayPort: 30071);

    [Fact]
    public async Task A_handler_that_always_fails_is_kept_after_the_bound_and_the_command_after_it_is_still_resumed()
    {
        var environment = await EnvironmentForAsync("poc_intent_bound", siloPort: 11183, gatewayPort: 30072);
        var failing = Guid.NewGuid();
        var after = Guid.NewGuid();

        await using var host = await PocHostProcess.StartAsync("intent", environment);
        await EnqueueAsync(host, $"enqueue-failing {failing}");
        await EnqueueAsync(host, $"enqueue {after} {HandlerDelayMs}");
        host.Kill();

        await using var restarted = await PocHostProcess.StartAsync("intent", environment);
        Assert.True(await WaitForAsync(restarted, $"applied {after}", "true", ResumeTimeout), Failure("the command after the failing one was not resumed", restarted));
        Assert.True(await WaitForAsync(restarted, $"intent {failing}", Bound, KeptTimeout), Failure("the failing command was not kept after the bound", restarted));
        Assert.Equal("false", await restarted.SendAsync($"applied {failing}"));
    }

    [Theory]
    [InlineData("enqueue", "poc_intent_long", 11184, 30073)]
    [InlineData("enqueue-heavy", "poc_intent_long_heavy", 11185, 30074)]
    [InlineData("enqueue-blocking", "poc_intent_long_blocking", 11336, 30226)]
    public async Task A_handler_that_runs_longer_than_the_grace_runs_once(string enqueueCommand, string database, int siloPort, int gatewayPort)
    {
        var environment = await EnvironmentForAsync(database, siloPort, gatewayPort);
        var aggregateId = Guid.NewGuid();

        await using var host = await PocHostProcess.StartAsync("intent", environment);
        await EnqueueAsync(host, $"{enqueueCommand} {aggregateId} {LongHandlerDelayMs}");

        Assert.True(await WaitForAsync(host, $"applied {aggregateId}", "true", ResumeTimeout), Failure("the long handler did not complete", host));
        Assert.True(await WaitForAsync(host, "outbox-count", "0", ResumeTimeout), Failure("the long handler's record was not removed", host));
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.Equal("1", await host.SendAsync($"applications {aggregateId}"));
    }

    [Fact]
    public async Task A_command_naming_its_aggregate_only_through_the_interface_is_resumed_in_its_aggregates_order()
    {
        var environment = await EnvironmentForAsync("poc_intent_order", siloPort: 11186, gatewayPort: 30075);
        var target = Guid.NewGuid();

        await using var host = await PocHostProcess.StartAsync("intent", environment);
        await EnqueueAsync(host, $"enqueue-ordered {target} 1 {HandlerDelayMs}");
        await EnqueueAsync(host, $"enqueue-ordered {target} 2 0");
        Assert.Equal(string.Empty, await host.SendAsync($"order {target}"));
        host.Kill();

        await using var restarted = await PocHostProcess.StartAsync("intent", environment);
        Assert.True(await WaitForAsync(restarted, $"order {target}", "1,2", ResumeTimeout), Failure($"the resumed commands did not run in order; last seen '{await restarted.SendAsync($"order {target}")}'", restarted));
    }

    [Fact]
    public async Task A_kept_command_an_operator_returns_is_resumed_with_its_count_starting_over()
    {
        var environment = await EnvironmentForAsync("poc_intent_return", siloPort: 11187, gatewayPort: 30076);
        var aggregateId = Guid.NewGuid();

        await using var host = await PocHostProcess.StartAsync("intent", environment);
        await EnqueueAsync(host, $"enqueue-failing {aggregateId}");
        Assert.True(await WaitForAsync(host, $"intent {aggregateId}", Bound, KeptTimeout), Failure("the failing command was not kept", host));

        await host.SendAsync($"heal {aggregateId}");
        Assert.Equal("1", await host.SendAsync($"return-kept {aggregateId}"));
        var afterReturn = await host.SendAsync($"intent {aggregateId}");
        Assert.True(afterReturn is "attempts=0 kept=false" or "attempts=1 kept=false" or "none", $"the returned command did not start over: '{afterReturn}'");

        Assert.True(await WaitForAsync(host, $"applied {aggregateId}", "true", ResumeTimeout), Failure("the returned command was not resumed", host));
        Assert.True(await WaitForAsync(host, $"intent {aggregateId}", "none", ResumeTimeout), Failure("the returned command's record was not removed", host));
    }

    private async Task RunKillsAsync(string enqueueCommand, string database, int siloPort, int gatewayPort)
    {
        var environment = await EnvironmentForAsync(database, siloPort, gatewayPort);

        for (var kill = 0; kill < Kills; kill++)
        {
            var aggregateId = Guid.NewGuid();

            await using var host = await PocHostProcess.StartAsync("intent", environment);
            await EnqueueAsync(host, $"{enqueueCommand} {aggregateId} {HandlerDelayMs}");
            Assert.Equal("false", await host.SendAsync($"applied {aggregateId}"));
            host.Kill();

            await using var restarted = await PocHostProcess.StartAsync("intent", environment);
            var applied = await WaitForAsync(restarted, $"applied {aggregateId}", "true", ResumeTimeout);

            Assert.True(applied, $"Kill {kill + 1}: the command was not applied after the restart. Log:{Environment.NewLine}{string.Join(Environment.NewLine, restarted.Log)}");
            // The record is deleted one completion window after the handler, not inside its turn.
            Assert.True(await WaitForAsync(restarted, "outbox-count", "0", ResumeTimeout), $"Kill {kill + 1}: the intent's record was not deleted after the handler completed.");
        }
    }

    private async Task<Dictionary<string, string>> EnvironmentForAsync(string database, int siloPort, int gatewayPort)
    {
        var store = postgres.ConnectionStringFor(database);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        return PocHostSettings.ToEnvironment(
            store,
            postgres.ConnectionStringFor("poc_orleans"),
            redis.ConnectionString,
            rabbit.ConnectionString,
            siloPort,
            gatewayPort);
    }

    private static async Task EnqueueAsync(PocHostProcess host, string command)
    {
        var reply = await host.SendAsync(command);
        Assert.True(reply.StartsWith("enqueued ", StringComparison.Ordinal), $"'{command}' replied '{reply}'. Log:{Environment.NewLine}{string.Join(Environment.NewLine, host.Log.TakeLast(40))}");
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

            await Task.Delay(500);
        }

        return false;
    }
}
