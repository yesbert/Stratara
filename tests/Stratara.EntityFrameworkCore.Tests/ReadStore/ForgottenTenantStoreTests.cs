using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.ForgottenTenants;

namespace Stratara.EventSourcing.EntityFrameworkCore.Tests.ReadStore;

/// <summary>
/// <c>projections</c> → <em>A projection can forget a deleted tenant</em>: the read store keeps each
/// projection's deleted tenants apart, records them idempotently, and empties one projection's record or
/// all of them — verified on SQLite.
/// </summary>
public sealed class ForgottenTenantStoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ForgottenTenantStore<StoreReadContext> _store;

    public ForgottenTenantStoreTests()
    {
        _connection.Open();
        var factory = new ContextFactory(new DbContextOptionsBuilder<StoreReadContext>().UseSqlite(_connection).Options);
        using (var context = factory.CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        _store = new ForgottenTenantStore<StoreReadContext>(factory);
    }

    public void Dispose() => _connection.Dispose();

    private sealed class StoreReadContext(DbContextOptions<StoreReadContext> options)
        : ReadDbContext<StoreReadContext>(options);

    private sealed class ContextFactory(DbContextOptions<StoreReadContext> options) : IDbContextFactory<StoreReadContext>
    {
        public StoreReadContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task A_forgotten_tenant_is_known_to_its_projection_only()
    {
        var tenant = Guid.CreateVersion7();

        await _store.ForgetAsync("Entries", [tenant]);

        Assert.True(await _store.HasForgottenAsync("Entries", tenant));
        Assert.False(await _store.HasForgottenAsync("Customers", tenant));
        Assert.False(await _store.HasForgottenAsync("Entries", Guid.CreateVersion7()));
    }

    [Fact]
    public async Task Forgetting_is_idempotent_across_repeated_and_overlapping_calls()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        await _store.ForgetAsync("Entries", [first]);
        await _store.ForgetAsync("Entries", [first]);
        await _store.ForgetAsync("Entries", [first, second, second]);

        Assert.True(await _store.HasForgottenAsync("Entries", first));
        Assert.True(await _store.HasForgottenAsync("Entries", second));
        await using var context = new StoreReadContext(new DbContextOptionsBuilder<StoreReadContext>().UseSqlite(_connection).Options);
        Assert.Equal(2, await context.Set<ForgottenTenant>().CountAsync());
    }

    [Fact]
    public async Task Clearing_one_projection_keeps_the_others()
    {
        var tenant = Guid.CreateVersion7();
        await _store.ForgetAsync("Entries", [tenant]);
        await _store.ForgetAsync("Customers", [tenant]);

        await _store.ClearAsync("Entries");

        Assert.False(await _store.HasForgottenAsync("Entries", tenant));
        Assert.True(await _store.HasForgottenAsync("Customers", tenant));
    }

    [Fact]
    public async Task Clearing_all_empties_every_projection()
    {
        var tenant = Guid.CreateVersion7();
        await _store.ForgetAsync("Entries", [tenant]);
        await _store.ForgetAsync("Customers", [tenant]);

        await _store.ClearAllAsync();

        Assert.False(await _store.HasForgottenAsync("Entries", tenant));
        Assert.False(await _store.HasForgottenAsync("Customers", tenant));
    }
}
