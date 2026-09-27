using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Session;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.Extensions;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;
using Xunit;

namespace Stratara.Testing.EntityFrameworkCore.Tests;

/// <summary>
/// Scenario <em>One stream is worked on through a filtered write context</em>. The write context filters every
/// tenant-scoped entity to the session's data-owner tenant, as the tenant-isolation guide switches the filter on, and a
/// snapshot is taken after every save. One stream holds events of two owners. Under either owner's session, and
/// without a session, the framework finds the stream, rebuilds it from every entry, snapshots it in full and appends to
/// it at its true version.
/// </summary>
public class TenantFilteredStreamTests
{
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task A_stream_with_two_owners_is_read_snapshotted_and_appended_to_in_full()
    {
        await using var host = EventStoreTestHost.Create<TenantFilteredWriteDbContext>(services =>
        {
            services.AddAggregatesFromAssemblyContaining<Account>();
            services.AddSingleton<ISnapshotStrategy>(new AlwaysSnapshotStrategy());
        });
        var accountId = Guid.CreateVersion7();
        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Account>(accountId, new AccountOpened(accountId, EventStoreTestHost.DefaultTenantId, "Ada", 100m));
            await events.SaveChangesAsync();
        });
        await host.ExecuteAsync(async events =>
        {
            await events.AppendAsync<Account>(accountId, new AmountDeposited(10m));
            await events.AppendOnBehalfOfAsync<Account>(accountId, new AmountDeposited(1000m), new EventSubject(Other));
            await events.SaveChangesAsync();
        });

        await host.ExecuteAsync(async events =>
        {
            await events.AppendAsync<Account>(accountId, new AmountDeposited(1m));
            await events.SaveChangesAsync();
        });

        Assert.Equal(1111m, (await host.AggregateAsync<Account>(accountId))!.Balance);
        host.Session.Set(TestSessionContext.ForTenant(Other));
        Assert.Equal(1111m, (await host.AggregateAsync<Account>(accountId))!.Balance);
        host.Session.Clear();
        Assert.Equal(1111m, (await host.AggregateAsync<Account>(accountId))!.Balance);
        await host.ExecuteAsync(async events => Assert.True(await events.ExistsAsync(accountId)));
    }

    private sealed class AlwaysSnapshotStrategy : ISnapshotStrategy
    {
        public bool ShouldSnapshot(Type aggregateType, long currentVersion, long lastSnapshotVersion) => true;
    }

    /// <summary>
    /// A write context that filters every tenant-scoped entity to the session's data-owner tenant, the way the
    /// tenant-isolation guide switches the filter on; without a session, to the empty identifier.
    /// </summary>
    private sealed class TenantFilteredWriteDbContext(DbContextOptions<TenantFilteredWriteDbContext> options, ISessionContextProvider sessions)
        : WriteDbContext<TenantFilteredWriteDbContext>(options), ITenantScopedDbContext
    {
        public Guid TenantId => sessions.Current?.TenantId ?? Guid.Empty;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyGlobalTenantQueryFilters(this);
        }
    }
}
