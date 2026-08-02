using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Xunit;

namespace Stratara.Testing.EntityFrameworkCore.Tests;

/// <summary>
/// Guards the invariant that a snapshot of aggregate type X may only ever rehydrate type X, even
/// when two aggregate types are (mis)keyed on the same stream id. Regression cover for a snapshot
/// read that filtered only by stream id and could return a higher-versioned snapshot of a foreign
/// type, deserializing it into the requested type and returning corrupt/default state.
/// </summary>
public class SnapshotTypeIsolationTests
{
    private sealed class AlwaysSnapshotStrategy : ISnapshotStrategy
    {
        public bool ShouldSnapshot(Type aggregateType, long currentVersion, long lastSnapshotVersion) => true;
    }

    private static EventStoreTestHost CreateHost() =>
        EventStoreTestHost.Create(s =>
        {
            s.AddAggregatesFromAssemblyContaining<Account>();
            s.AddSingleton<ISnapshotStrategy>(new AlwaysSnapshotStrategy());
        });

    [Fact]
    public async Task Foreign_type_snapshot_on_a_shared_stream_does_not_corrupt_the_load()
    {
        var sharedId = Guid.CreateVersion7();
        await using var host = CreateHost();

        // Account owns the stream at v1 and snapshots there.
        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Account>(sharedId,
                new AccountOpened(sharedId, EventStoreTestHost.DefaultTenantId, "Ada", 100m));
            await events.SaveChangesAsync();
        });

        // A different aggregate type is keyed on the same stream and snapshots at a higher version.
        await host.ExecuteAsync(async events =>
        {
            await events.AppendAsync<MetricsProbe>(sharedId, new MetricsProbeTouched());
            await events.SaveChangesAsync();
        });

        var account = await host.AggregateAsync<Account>(sharedId);

        Assert.NotNull(account);
        Assert.Equal("Ada", account!.Owner);
        Assert.Equal(100m, account.Balance);
    }

    [Fact]
    public async Task Each_type_on_a_shared_stream_rehydrates_from_its_own_snapshot()
    {
        var sharedId = Guid.CreateVersion7();
        await using var host = CreateHost();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Account>(sharedId,
                new AccountOpened(sharedId, EventStoreTestHost.DefaultTenantId, "Ada", 100m));
            await events.SaveChangesAsync();
        });

        await host.ExecuteAsync(async events =>
        {
            await events.AppendAsync<MetricsProbe>(sharedId, new MetricsProbeTouched());
            await events.AppendAsync<MetricsProbe>(sharedId, new MetricsProbeTouched());
            await events.SaveChangesAsync();
        });

        var account = await host.AggregateAsync<Account>(sharedId);
        var probe = await host.AggregateAsync<MetricsProbe>(sharedId);

        Assert.Equal("Ada", account!.Owner);
        Assert.Equal(100m, account.Balance);
        Assert.Equal(2, probe!.Touches);
    }
}
