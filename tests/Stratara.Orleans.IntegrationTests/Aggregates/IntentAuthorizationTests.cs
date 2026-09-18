using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Timers;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// Scenario <em>A resumed command is authorized from its recorded session</em>: a role-guarded command recorded by a host
/// that died before the hand-over is resumed on a silo, where no web request exists. A provider that answers from the
/// session context authorizes it and the handler runs; a provider that answers from the current request refuses every
/// attempt, each logged, and the command is kept. The hosts run as separate processes, because a role-guarded command
/// type loaded into the test process would fail the start of every other host in it.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class IntentAuthorizationTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const string Administrator = "0f6c1a9e-6a7d-4b8e-9d1c-3a2b4c5d6e7f";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_session_driven_provider_authorizes_the_resumed_command()
    {
        await using var host = await PocHostProcess.StartAsync("intent-authorization-session", await EnvironmentForAsync("poc_intent_authorization_session", 11341, 30231));
        var probe = Guid.NewGuid();

        await host.SendAsync($"record {probe} {Administrator}");

        Assert.True(await WaitForAsync(host, $"ran {probe}", answer => answer == "true"), $"the resumed command was not handled. Log:{Environment.NewLine}{string.Join(Environment.NewLine, host.Log)}");
    }

    [Fact]
    public async Task A_request_bound_provider_refuses_every_attempt_and_the_command_is_kept()
    {
        await using var host = await PocHostProcess.StartAsync("intent-authorization-request", await EnvironmentForAsync("poc_intent_authorization_request", 11342, 30232));
        var probe = Guid.NewGuid();

        var intent = await host.SendAsync($"record {probe} {Administrator}");

        Assert.True(await WaitForAsync(host, $"intent {intent}", answer => answer.Contains("kept=true", StringComparison.Ordinal)), $"the refused command was not kept: {await host.SendAsync($"intent {intent}")}");
        var state = await host.SendAsync($"intent {intent}");
        var attempts = int.Parse(state.Split(' ')[0]["attempts=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(attempts > 1, state);
        Assert.Contains("AuthorizationException", state, StringComparison.Ordinal);
        // The record counts the dispatch's hand-over, which a host that only recorded never made, as its first attempt.
        Assert.Equal((attempts - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), await host.SendAsync($"attempt-logs {intent}"));
        Assert.Equal("false", await host.SendAsync($"ran {probe}"));
    }

    private async Task<Dictionary<string, string>> EnvironmentForAsync(string database, int siloPort, int gatewayPort)
    {
        var store = postgres.ConnectionStringFor(database);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        return PocHostSettings.ToEnvironment(store, postgres.ConnectionStringFor("poc_orleans"), redis.ConnectionString, rabbit.ConnectionString, siloPort, gatewayPort);
    }

    private static async Task<bool> WaitForAsync(PocHostProcess host, string command, Func<string, bool> answered)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (answered(await host.SendAsync(command)))
            {
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }
}
