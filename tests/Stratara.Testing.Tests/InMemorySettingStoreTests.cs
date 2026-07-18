using Stratara.Abstractions.Settings;
using Xunit;

namespace Stratara.Testing.Tests;

public class InMemorySettingStoreTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();

    [Fact]
    public async Task Set_get_is_exact_scope_and_null_deletes()
    {
        var store = new InMemorySettingStore();

        await store.SetAsync("Ui.Theme", "dark", SettingScope.ForUser(UserId));
        Assert.Equal("dark", await store.GetOrNullAsync("Ui.Theme", SettingScope.ForUser(UserId)));
        Assert.Null(await store.GetOrNullAsync("Ui.Theme", SettingScope.Global));

        await store.SetAsync("Ui.Theme", null, SettingScope.ForUser(UserId));
        Assert.Null(await store.GetOrNullAsync("Ui.Theme", SettingScope.ForUser(UserId)));
    }

    [Fact]
    public async Task GetAll_returns_the_exact_scope_only()
    {
        var store = new InMemorySettingStore();
        await store.SetAsync("A", "1", SettingScope.ForTenant(TenantId));
        await store.SetAsync("B", "2", SettingScope.ForTenant(TenantId));
        await store.SetAsync("A", "global", SettingScope.Global);

        var all = await store.GetAllAsync(SettingScope.ForTenant(TenantId));
        Assert.Equal(2, all.Count);
        Assert.Equal("1", all["A"]);
    }

    [Fact]
    public async Task Delete_scope_sweeps_by_dimension()
    {
        var store = new InMemorySettingStore();
        await store.SetAsync("X", "user", SettingScope.ForUser(UserId));
        await store.SetAsync("X", "uit", SettingScope.ForUserInTenant(TenantId, UserId));
        await store.SetAsync("X", "tenant", SettingScope.ForTenant(TenantId));
        await store.SetAsync("X", "global", SettingScope.Global);

        await store.DeleteScopeAsync(SettingScope.ForUser(UserId));
        Assert.Null(await store.GetOrNullAsync("X", SettingScope.ForUser(UserId)));
        Assert.Null(await store.GetOrNullAsync("X", SettingScope.ForUserInTenant(TenantId, UserId)));
        Assert.Equal("tenant", await store.GetOrNullAsync("X", SettingScope.ForTenant(TenantId)));

        await store.DeleteScopeAsync(SettingScope.Global);
        Assert.Null(await store.GetOrNullAsync("X", SettingScope.Global));
        Assert.Equal("tenant", await store.GetOrNullAsync("X", SettingScope.ForTenant(TenantId)));
    }
}
