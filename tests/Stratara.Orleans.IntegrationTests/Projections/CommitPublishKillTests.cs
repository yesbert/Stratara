using System.Text.Json;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Timers;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// T1 of the expectations: the host is ended between the commit and the publish, twenty times per
/// path. On the bus path the bundle in flight is lost and the view never appears; on the checkpoint
/// path every event is applied after the restart. The raw counts are written to the evidence
/// directory when <c>POC_EVIDENCE_DIR</c> names one; otherwise the run asserts and records nothing.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class CommitPublishKillTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int Kills = 20;
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);
    private const int ViewTimeoutMs = 15_000;

    [Fact]
    public async Task The_checkpoint_path_loses_no_event_where_the_bus_path_loses_every_bundle_in_flight()
    {
        var busLost = await RunAsync("projection-bus", "poc_kill_bus", siloPort: 11201, gatewayPort: 30090);
        var grainLost = await RunAsync("projection-grain", "poc_kill_grain", siloPort: 11202, gatewayPort: 30091);

        TestContext.Current.TestOutputHelper?.WriteLine($"bus path: {busLost.Count} of {Kills} events lost; checkpoint path: {grainLost.Count} of {Kills} events lost");
        WriteEvidence(busLost, grainLost);

        Assert.Empty(grainLost);
    }

    private async Task<List<Guid>> RunAsync(string scenario, string database, int siloPort, int gatewayPort)
    {
        var store = postgres.ConnectionStringFor(database);
        var read = postgres.ConnectionStringFor(database + "_read");
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(read);
        var environment = PocHostSettings.ToEnvironment(
            store,
            postgres.ConnectionStringFor("poc_orleans"),
            redis.ConnectionString,
            rabbit.ConnectionString,
            siloPort,
            gatewayPort,
            read);

        var lost = new List<Guid>();
        for (var kill = 0; kill < Kills; kill++)
        {
            var streamId = Guid.NewGuid();

            var host = await PocHostProcess.StartAsync(scenario, environment);
            Assert.Equal("ok", await host.SendAsync("arm-kill"));
            await host.SendExpectingExitAsync($"append {streamId}", ExitTimeout);

            await using var restarted = await PocHostProcess.StartAsync(scenario, environment);
            if (await restarted.SendAsync($"view {streamId} {ViewTimeoutMs}") == "absent")
            {
                lost.Add(streamId);
            }
        }

        return lost;
    }

    private static void WriteEvidence(List<Guid> busLost, List<Guid> grainLost)
    {
        var directory = Environment.GetEnvironmentVariable("POC_EVIDENCE_DIR");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        var run = Path.Combine(directory, "commit-publish-kill", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(run);
        var result = new
        {
            kills = Kills,
            bus = new { lost = busLost.Count, streams = busLost },
            checkpoint = new { lost = grainLost.Count, streams = grainLost },
        };
        File.WriteAllText(Path.Combine(run, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
