using Stratara.Abstractions.Settings;
using Xunit;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

public class EfSettingStoreTests
{
    private static readonly Guid TenantA = Guid.CreateVersion7();
    private static readonly Guid TenantB = Guid.CreateVersion7();
    private static readonly Guid UserA = Guid.CreateVersion7();

    [Fact]
    public async Task Set_then_get_is_exact_scope()
    {
        using var fixture = new SqliteDirectoryFixture();
        var store = fixture.SettingStore;

        await store.SetAsync("Ui.Theme", "dark", SettingScope.ForUser(UserA));

        Assert.Equal("dark", await store.GetOrNullAsync("Ui.Theme", SettingScope.ForUser(UserA)));
        Assert.Null(await store.GetOrNullAsync("Ui.Theme", SettingScope.Global));
        Assert.Null(await store.GetOrNullAsync("Ui.Theme", SettingScope.ForUserInTenant(TenantA, UserA)));
    }

    [Fact]
    public async Task Set_overwrites_and_null_deletes()
    {
        using var fixture = new SqliteDirectoryFixture();
        var store = fixture.SettingStore;
        var scope = SettingScope.ForTenant(TenantA);

        await store.SetAsync("Limits.MaxUsers", "10", scope);
        await store.SetAsync("Limits.MaxUsers", "25", scope);
        Assert.Equal("25", await store.GetOrNullAsync("Limits.MaxUsers", scope));

        await store.SetAsync("Limits.MaxUsers", null, scope);
        Assert.Null(await store.GetOrNullAsync("Limits.MaxUsers", scope));
    }

    [Fact]
    public async Task GetAll_returns_only_the_exact_scope()
    {
        using var fixture = new SqliteDirectoryFixture();
        var store = fixture.SettingStore;

        await store.SetAsync("A", "global", SettingScope.Global);
        await store.SetAsync("A", "tenant", SettingScope.ForTenant(TenantA));
        await store.SetAsync("B", "tenant", SettingScope.ForTenant(TenantA));

        var all = await store.GetAllAsync(SettingScope.ForTenant(TenantA));
        Assert.Equal(2, all.Count);
        Assert.Equal("tenant", all["A"]);

        Assert.Single(await store.GetAllAsync(SettingScope.Global));
        Assert.Empty(await store.GetAllAsync(SettingScope.ForTenant(TenantB)));
    }

    [Fact]
    public async Task DeleteScope_user_sweeps_across_tenants()
    {
        using var fixture = new SqliteDirectoryFixture();
        var store = fixture.SettingStore;

        await store.SetAsync("Ui.Theme", "dark", SettingScope.ForUser(UserA));
        await store.SetAsync("Ui.Theme", "light", SettingScope.ForUserInTenant(TenantA, UserA));
        await store.SetAsync("Ui.Theme", "amber", SettingScope.ForUserInTenant(TenantB, UserA));
        await store.SetAsync("Ui.Theme", "system", SettingScope.Global);

        await store.DeleteScopeAsync(SettingScope.ForUser(UserA));

        Assert.Null(await store.GetOrNullAsync("Ui.Theme", SettingScope.ForUser(UserA)));
        Assert.Null(await store.GetOrNullAsync("Ui.Theme", SettingScope.ForUserInTenant(TenantA, UserA)));
        Assert.Null(await store.GetOrNullAsync("Ui.Theme", SettingScope.ForUserInTenant(TenantB, UserA)));
        Assert.Equal("system", await store.GetOrNullAsync("Ui.Theme", SettingScope.Global));
    }

    [Fact]
    public async Task DeleteScope_tenant_sweeps_across_users_and_global_scope_only_global()
    {
        using var fixture = new SqliteDirectoryFixture();
        var store = fixture.SettingStore;

        await store.SetAsync("X", "1", SettingScope.ForTenant(TenantA));
        await store.SetAsync("X", "2", SettingScope.ForUserInTenant(TenantA, UserA));
        await store.SetAsync("X", "3", SettingScope.ForUser(UserA));
        await store.SetAsync("X", "4", SettingScope.Global);

        await store.DeleteScopeAsync(SettingScope.ForTenant(TenantA));
        Assert.Null(await store.GetOrNullAsync("X", SettingScope.ForTenant(TenantA)));
        Assert.Null(await store.GetOrNullAsync("X", SettingScope.ForUserInTenant(TenantA, UserA)));
        Assert.Equal("3", await store.GetOrNullAsync("X", SettingScope.ForUser(UserA)));

        await store.DeleteScopeAsync(SettingScope.Global);
        Assert.Null(await store.GetOrNullAsync("X", SettingScope.Global));
        Assert.Equal("3", await store.GetOrNullAsync("X", SettingScope.ForUser(UserA)));
    }
}
