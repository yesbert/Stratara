using Stratara.Abstractions.CommitOrder;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Timers;
using Stratara.Shared.Partitioning;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// Scenario <em>A silo dies before a deferred checkpoint is written</em>: a projection's reader reads an entry it applies
/// nothing for and holds its checkpoint back; the process is killed before it writes it. The reader started again in a
/// new process reads that entry again from the checkpoint it left, applies nothing for it, and applies the fact committed
/// after it once — the unguarded running total counts every application.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class DeferredCheckpointKillTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const string Scenario = "projection-grain";
    private const string View = nameof(CounterViewProjection);
    private const string Totals = nameof(CounterTotalsProjection);
    private const int ViewTimeoutMs = 15_000;
    private static readonly int PartitionCount = new CommitOrderOptions().PartitionCount;

    [Fact]
    public async Task A_reader_killed_while_it_held_its_checkpoint_back_reads_the_entries_again_and_applies_nothing_twice()
    {
        var store = postgres.ConnectionStringFor("poc_deferred_kill");
        var read = postgres.ConnectionStringFor("poc_deferred_kill_read");
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(read);
        var environment = PocHostSettings.ToEnvironment(store, postgres.ConnectionStringFor("poc_orleans"), redis.ConnectionString, rabbit.ConnectionString, 11564, 30564, read);
        var (first, unhandled, later) = ThreeInOnePartition();

        long held;
        await using (var host = await PocHostProcess.StartAsync(Scenario, environment))
        {
            Assert.Equal("ok", await host.SendAsync("reset-count"));
            Assert.Equal("ok", await host.SendAsync($"append {first}"));
            Assert.Equal("present", await host.SendAsync($"view {first} {ViewTimeoutMs}"));
            await host.SendAsync($"catch-up {View} {first}");
            await host.SendAsync($"catch-up {Totals} {first}");
            held = long.Parse(await host.SendAsync($"checkpoint {View} {first}"), System.Globalization.CultureInfo.InvariantCulture);

            Assert.Equal("ok", await host.SendAsync($"append-unhandled {unhandled}"));
            await host.SendAsync($"catch-up {View} {unhandled}");
            await host.SendAsync($"catch-up {Totals} {unhandled}");
            Assert.Equal(held.ToString(System.Globalization.CultureInfo.InvariantCulture), await host.SendAsync($"checkpoint {View} {unhandled}"));
            host.Kill();
        }

        await using (var restarted = await PocHostProcess.StartAsync(Scenario, environment))
        {
            Assert.Equal("ok", await restarted.SendAsync($"append {later}"));
            Assert.Equal("present", await restarted.SendAsync($"view {later} {ViewTimeoutMs}"));
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            Assert.Equal("2", await restarted.SendAsync("count"));
            Assert.True(long.Parse(await restarted.SendAsync($"checkpoint {View} {later}"), System.Globalization.CultureInfo.InvariantCulture) > held);
        }
    }

    private static (Guid First, Guid Unhandled, Guid Later) ThreeInOnePartition()
    {
        var first = Guid.NewGuid();
        var partition = PartitionOf(first);
        return (first, InPartition(partition), InPartition(partition));
    }

    private static Guid InPartition(int partition)
    {
        while (true)
        {
            var streamId = Guid.NewGuid();
            if (PartitionOf(streamId) == partition)
            {
                return streamId;
            }
        }
    }

    private static int PartitionOf(Guid streamId) => PartitionMap.PartitionOf(BucketCalculator.GetBucketId(streamId), PartitionCount);
}
