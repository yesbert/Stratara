using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;

namespace Stratara.Orleans.IntegrationTests.Timers;

/// <summary>
/// T5 of the expectations: a host with open timers is killed and restarted, ten times. Before each
/// kill, five owners are kept and five removed. After the restart every kept owner's timer fires
/// exactly once, and no removed owner's timer fires at all. The due time is fixed by a start signal
/// before the first registration, and the kill is asserted to land before it with every timer registered
/// and none fired — so a slow run fails instead of passing on timers that fired before the kill.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class HardKillTimerTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private const int Kills = 10;
    private const int OwnersPerKind = 5;
    private static readonly TimeSpan DueIn = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SettleAfterRestart = PocSilo.RefreshReminderListPeriod * 2 + TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Timers_fire_once_for_kept_owners_and_never_for_removed_ones_after_a_kill()
    {
        var environment = PocHostSettings.ToEnvironment(
            postgres.ConnectionStringFor("poc_timers_store"),
            postgres.ConnectionStringFor("poc_orleans"),
            redis.ConnectionString,
            rabbit: string.Empty,
            siloPort: 11171,
            gatewayPort: 30060,
            // POC_MEMBERSHIP in the runner's environment selects the membership settings — R2 of
            // optimise-the-orleans-execution-model ran this test once under the shortened ones.
            membership: Enum.TryParse<PocSiloMembership>(Environment.GetEnvironmentVariable("POC_MEMBERSHIP"), out var membership) ? membership : PocSiloMembership.Default);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(postgres.ConnectionStringFor("poc_timers_store"));

        var report = new List<string>();
        for (var kill = 0; kill < Kills; kill++)
        {
            var iterationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var kept = Enumerable.Range(0, OwnersPerKind).Select(i => $"kept-{kill}-{i}-{Guid.NewGuid():N}").ToList();
            var removed = Enumerable.Range(0, OwnersPerKind).Select(i => $"removed-{kill}-{i}-{Guid.NewGuid():N}").ToList();

            await using var host = await PocHostProcess.StartAsync("timers", environment);
            var dueAt = DateTimeOffset.UtcNow + DueIn;
            foreach (var owner in kept.Concat(removed))
            {
                Assert.Equal("ok", await host.SendAsync($"add-owner {owner}"));
                Assert.Equal("ok", await host.SendAsync($"register-at {owner} expire {dueAt.ToUnixTimeMilliseconds()}"));
            }

            foreach (var owner in removed)
            {
                Assert.Equal("ok", await host.SendAsync($"remove-owner {owner}"));
            }

            foreach (var owner in kept)
            {
                Assert.Equal("1", await host.SendAsync($"timers {owner}"));
                Assert.Equal("0", await host.SendAsync($"firings {owner}"));
            }

            Assert.True(DateTimeOffset.UtcNow < dueAt, $"Kill {kill + 1}: the timers came due before the kill, so a firing after the restart would prove nothing.");
            host.Kill();
            var killedAt = System.Diagnostics.Stopwatch.GetTimestamp();

            await using var restarted = await PocHostProcess.StartAsync("timers", environment);
            var restartSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(killedAt).TotalSeconds;
            var settleUntil = dueAt + SettleAfterRestart;
            if (settleUntil > DateTimeOffset.UtcNow)
            {
                await Task.Delay(settleUntil - DateTimeOffset.UtcNow);
            }

            foreach (var owner in kept)
            {
                Assert.Equal("1", await restarted.SendAsync($"firings {owner}"));
                Assert.Equal("0", await restarted.SendAsync($"timers {owner}"));
            }

            foreach (var owner in removed)
            {
                Assert.Equal("0", await restarted.SendAsync($"firings {owner}"));
                Assert.Equal("0", await restarted.SendAsync($"timers {owner}"));
            }

            report.Add($"kill {kill + 1}/{Kills}: {OwnersPerKind} kept fired once, {OwnersPerKind} removed fired never; restart took {restartSeconds:F1} s, iteration {System.Diagnostics.Stopwatch.GetElapsedTime(iterationStarted).TotalSeconds:F1} s so far");
        }

        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, report));
        Console.WriteLine(string.Join(Environment.NewLine, report));
    }
}
