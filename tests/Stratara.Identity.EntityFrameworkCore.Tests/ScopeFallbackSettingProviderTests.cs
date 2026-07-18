using Microsoft.Extensions.Configuration;
using Stratara.Abstractions.Settings;
using Stratara.Testing;
using Xunit;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

public class ScopeFallbackSettingProviderTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();

    private static SettingCatalog Catalog(params SettingDefinition[] definitions)
    {
        var catalog = new SettingCatalog();
        catalog.Add(definitions);
        return catalog;
    }

    private static ScopeFallbackSettingProvider Provider(
        ISettingStore store,
        SettingCatalog catalog,
        bool withSession = true,
        Dictionary<string, string?>? configValues = null)
    {
        var configuration = configValues is null
            ? null
            : new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        return new ScopeFallbackSettingProvider(
            store,
            catalog,
            withSession ? TestSessionContextProvider.ForTenant(TenantId, UserId) : null,
            configuration);
    }

    [Fact]
    public async Task Most_specific_scope_wins()
    {
        var store = new InMemorySettingStore();
        await store.SetAsync("Ui.Theme", "global", SettingScope.Global);
        await store.SetAsync("Ui.Theme", "tenant", SettingScope.ForTenant(TenantId));
        await store.SetAsync("Ui.Theme", "user", SettingScope.ForUser(UserId));
        await store.SetAsync("Ui.Theme", "user-in-tenant", SettingScope.ForUserInTenant(TenantId, UserId));

        var provider = Provider(store, Catalog(new SettingDefinition("Ui.Theme")));

        Assert.Equal("user-in-tenant", await provider.GetOrNullAsync("Ui.Theme"));
    }

    [Fact]
    public async Task Chain_falls_back_scope_by_scope()
    {
        var store = new InMemorySettingStore();
        await store.SetAsync("Ui.Theme", "tenant", SettingScope.ForTenant(TenantId));

        var provider = Provider(store, Catalog(new SettingDefinition("Ui.Theme")));

        Assert.Equal("tenant", await provider.GetOrNullAsync("Ui.Theme"));
    }

    [Fact]
    public async Task Configuration_beats_the_code_default_but_not_stored_values()
    {
        var store = new InMemorySettingStore();
        var catalog = Catalog(new SettingDefinition("Limits.MaxUsers", "5"));
        var config = new Dictionary<string, string?> { ["Stratara:Settings:Limits.MaxUsers"] = "50" };

        var provider = Provider(store, catalog, configValues: config);
        Assert.Equal("50", await provider.GetOrNullAsync("Limits.MaxUsers"));

        await store.SetAsync("Limits.MaxUsers", "10", SettingScope.Global);
        var freshProvider = Provider(store, catalog, configValues: config);
        Assert.Equal("10", await freshProvider.GetOrNullAsync("Limits.MaxUsers"));
    }

    [Fact]
    public async Task Code_default_is_the_last_resort_and_typed_access_converts()
    {
        var provider = Provider(
            new InMemorySettingStore(),
            Catalog(new SettingDefinition("Limits.MaxUsers", "5"), new SettingDefinition("Feature.On", "true")));

        Assert.Equal(5, await provider.GetAsync<int>("Limits.MaxUsers"));
        Assert.True(await provider.GetAsync<bool>("Feature.On"));
        Assert.Equal("5", await provider.GetOrNullAsync("Limits.MaxUsers"));
    }

    [Fact]
    public async Task Non_inherited_settings_consult_only_the_most_specific_scope()
    {
        var store = new InMemorySettingStore();
        await store.SetAsync("Ui.Theme", "tenant", SettingScope.ForTenant(TenantId));
        await store.SetAsync("Ui.Theme", "global", SettingScope.Global);

        var provider = Provider(
            store, Catalog(new SettingDefinition("Ui.Theme", "system", IsInherited: false)));

        Assert.Equal("system", await provider.GetOrNullAsync("Ui.Theme"));

        await store.SetAsync("Ui.Theme", "mine", SettingScope.ForUserInTenant(TenantId, UserId));
        var freshProvider = Provider(
            store, Catalog(new SettingDefinition("Ui.Theme", "system", IsInherited: false)));
        Assert.Equal("mine", await freshProvider.GetOrNullAsync("Ui.Theme"));
    }

    [Fact]
    public async Task Without_a_session_only_the_global_scope_applies()
    {
        var store = new InMemorySettingStore();
        await store.SetAsync("Ui.Theme", "tenant", SettingScope.ForTenant(TenantId));
        await store.SetAsync("Ui.Theme", "global", SettingScope.Global);

        var provider = Provider(store, Catalog(new SettingDefinition("Ui.Theme")), withSession: false);

        Assert.Equal("global", await provider.GetOrNullAsync("Ui.Theme"));
    }

    [Fact]
    public async Task Undeclared_names_throw()
    {
        var provider = Provider(new InMemorySettingStore(), Catalog());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetOrNullAsync("Not.Declared"));
        Assert.Contains("Not.Declared", ex.Message);
    }

    [Fact]
    public async Task Missing_value_without_default_returns_null_and_typed_fallback()
    {
        var provider = Provider(new InMemorySettingStore(), Catalog(new SettingDefinition("Optional.Value")));

        Assert.Null(await provider.GetOrNullAsync("Optional.Value"));
        Assert.Equal(42, await provider.GetAsync("Optional.Value", 42));
    }
}
