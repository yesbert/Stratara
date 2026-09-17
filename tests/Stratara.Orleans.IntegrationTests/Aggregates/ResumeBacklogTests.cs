using Microsoft.Extensions.Hosting;
using Stratara.Orleans.IntegrationTests.Fixtures;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// Five batches' worth of recorded commands are due at once, as after an outage of the hosts that dispatch: the drain
/// hands every one of them over within one of its periods, not one batch per period (scenario <em>A backlog larger than
/// one batch is due</em>).
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ResumeBacklogTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int BatchSize = 20;
    private const int Batches = 5;
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_backlog_of_several_batches_is_handed_over_within_one_period()
    {
        var probes = new RecordedIntentProbes();
        using var host = await RecordedIntentHost.StartAsync(new RecordedIntentSettings(
            postgres.ConnectionStringFor("poc_intent_backlog_store"), postgres.ConnectionStringFor("poc_orleans"), redis.ConnectionString, rabbit.ConnectionString,
            11256, 30146, probes, PollingInterval, BatchSize));
        var tenant = Guid.NewGuid();
        var backlog = Enumerable.Range(0, BatchSize * Batches).Select(_ => Guid.NewGuid()).ToList();
        foreach (var probe in backlog)
        {
            await RecordedIntentHost.RecordAsync(host, tenant, probe);
        }

        Assert.True(
            await RecordedIntentHost.WaitUntilAsync(() => Task.FromResult(backlog.All(probes.Ran.ContainsKey)), PollingInterval * (Batches + 3)),
            $"only {backlog.Count(probes.Ran.ContainsKey)} of {backlog.Count} commands ran");
        var times = backlog.Select(probe => probes.Ran[probe].At).ToList();
        var span = times.Max() - times.Min();
        TestContext.Current.TestOutputHelper?.WriteLine($"{backlog.Count} commands in {Batches} batches ran over {span.TotalSeconds:F1} s at a period of {PollingInterval.TotalSeconds:F0} s");
        Assert.True(span < PollingInterval, $"the backlog ran over {span.TotalSeconds:F1} s, more than one period of {PollingInterval.TotalSeconds:F0} s");
        await host.StopAsync();
    }
}
