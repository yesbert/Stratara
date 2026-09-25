using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Settings;
using Xunit;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

public class SettingCatalogRegistrationTests
{
    [Fact]
    public async Task The_store_stands_without_declared_settings()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var services = new ServiceCollection();
        services.AddDbContext<TestDirectoryDbContext>(o => o.UseSqlite(connection));
        services.AddSettingStore<TestDirectoryDbContext>();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TestDirectoryDbContext>().Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var store = scope.ServiceProvider.GetRequiredService<ISettingStore>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingProvider>();
        var user = SettingScope.ForUser(Guid.CreateVersion7());
        await store.SetAsync("Ui.Theme", "dark", user, TestContext.Current.CancellationToken);

        await store.DeleteScopeAsync(user, TestContext.Current.CancellationToken);

        Assert.Empty(await store.GetAllAsync(user, TestContext.Current.CancellationToken));
        var read = await Assert.ThrowsAsync<InvalidOperationException>(
            () => settings.GetOrNullAsync("Ui.Theme", TestContext.Current.CancellationToken));
        Assert.Contains("not declared", read.Message);
    }

    [Fact]
    public void Parts_declared_before_and_after_the_store_form_one_catalog()
    {
        var services = new ServiceCollection();
        services.AddSettingCatalog(c => c.Add(new SettingDefinition("Ui.Theme", "system")));
        services.AddSettingStore<TestDirectoryDbContext>();
        services.AddSettingCatalog(c => c.Add(new SettingDefinition("Billing.VatId", IsInherited: false)));

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<SettingCatalog>();

        Assert.True(catalog.Contains("Ui.Theme"));
        Assert.True(catalog.Contains("Billing.VatId"));
        Assert.Single(services, d => d.ServiceType == typeof(SettingCatalog));
    }

    [Fact]
    public void A_part_declared_after_the_store_adds_to_its_empty_catalog()
    {
        var services = new ServiceCollection();
        services.AddSettingStore<TestDirectoryDbContext>();
        services.AddSettingCatalog(c => c.Add(new SettingDefinition("Ui.Theme", "system")));

        using var provider = services.BuildServiceProvider();

        Assert.True(provider.GetRequiredService<SettingCatalog>().Contains("Ui.Theme"));
    }

    [Fact]
    public void A_name_declared_in_two_parts_fails_at_registration()
    {
        var services = new ServiceCollection();
        services.AddSettingCatalog(c => c.Add(new SettingDefinition("Ui.Theme", "system")));

        var ex = Assert.Throws<ArgumentException>(
            () => services.AddSettingCatalog(c => c.Add(new SettingDefinition("Ui.Theme", "dark"))));

        Assert.Contains("Ui.Theme", ex.Message);
    }

    [Fact]
    public void A_catalog_registered_as_a_factory_cannot_be_added_to()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => new SettingCatalog());

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddSettingCatalog(c => c.Add(new SettingDefinition("Ui.Theme"))));

        Assert.Contains(nameof(IdentityDirectoryServiceCollectionExtensions.AddSettingCatalog), ex.Message);
    }
}
