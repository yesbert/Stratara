using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;

namespace Stratara.Orleans.IntegrationTests.Timers;

/// <summary>
/// T5 of the expectations: a host with open timers is killed and restarted, ten times. Before each
/// kill, five owners are kept and five removed. After the restart every kept owner's timer fires
/// exactly once within two refresh periods, and no removed owner's timer fires at all.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class HardKillTimerTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private const int Kills = 10;
    private const int OwnersPerKind = 5;
    private const int DueInMs = 3_000;
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
            // The test cluster's default is the short setting; POC_MEMBERSHIP in the runner's
            // environment overrides it — R2 of optimise-the-orleans-execution-model used that.
            membership: Enum.TryParse<PocSiloMembership>(Environment.GetEnvironmentVariable("POC_MEMBERSHIP"), out var membership) ? membership : PocSiloMembership.ShortIAmAlive);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(postgres.ConnectionStringFor("poc_timers_store"));

        var report = new List<string>();
        for (var kill = 0; kill < Kills; kill++)
        {
            var iterationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var kept = Enumerable.Range(0, OwnersPerKind).Select(i => $"kept-{kill}-{i}-{Guid.NewGuid():N}").ToList();
            var removed = Enumerable.Range(0, OwnersPerKind).Select(i => $"removed-{kill}-{i}-{Guid.NewGuid():N}").ToList();

            var host = await PocHostProcess.StartAsync("timers", environment);
            foreach (var owner in kept.Concat(removed))
            {
                Assert.Equal("ok", await host.SendAsync($"add-owner {owner}"));
                Assert.Equal("ok", await host.SendAsync($"register {owner} expire {DueInMs}"));
            }

            foreach (var owner in removed)
            {
                Assert.Equal("ok", await host.SendAsync($"remove-owner {owner}"));
            }

            host.Kill();
            var killedAt = System.Diagnostics.Stopwatch.GetTimestamp();

            await using var restarted = await PocHostProcess.StartAsync("timers", environment);
            var restartSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(killedAt).TotalSeconds;
            await Task.Delay(SettleAfterRestart);

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

/// <summary>Creates the store database the timers host writes its owners and firings to.</summary>
internal static class PostgresTimerHostSchema
{
    public static async Task EnsureDatabaseAsync(string connectionString)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        var database = builder.Database!;
        builder.Database = "postgres";
        await using var connection = new Npgsql.NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var exists = new Npgsql.NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        exists.Parameters.AddWithValue("name", database);
        if (await exists.ExecuteScalarAsync() is null)
        {
            await using var create = new Npgsql.NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            await create.ExecuteNonQueryAsync();
        }
    }
}
