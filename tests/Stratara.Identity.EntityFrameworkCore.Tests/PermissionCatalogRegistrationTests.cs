using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Authorization;
using Xunit;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

public class PermissionCatalogRegistrationTests
{
    [Fact]
    public void Parts_add_up_and_grants_to_one_role_accumulate()
    {
        var services = new ServiceCollection();
        services.AddPermissionCatalog(c =>
        {
            c.Add("sims.read", "sims.write");
            c.GrantToRole("TenantAdmin", "sims.read", "sims.write");
        });
        services.AddPermissionCatalog(c =>
        {
            c.Add("billing.read");
            c.GrantToRole("TenantAdmin", "billing.read");
        });

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<PermissionCatalog>();

        Assert.Equal(["billing.read", "sims.read", "sims.write"], catalog.All.Order());
        Assert.Equal(["billing.read", "sims.read", "sims.write"], catalog.GetRolePermissions("TenantAdmin").Order());
        Assert.Single(services, d => d.ServiceType == typeof(PermissionCatalog));
    }

    [Fact]
    public void A_later_part_may_grant_a_permission_an_earlier_part_declared()
    {
        var services = new ServiceCollection();
        services.AddPermissionCatalog(c => c.Add("sims.read"));
        services.AddPermissionCatalog(c => c.GrantToRole("Support", "sims.read"));

        using var provider = services.BuildServiceProvider();

        Assert.Contains("sims.read", provider.GetRequiredService<PermissionCatalog>().GetRolePermissions("Support"));
    }

    [Fact]
    public void A_permission_redeclared_in_a_later_part_has_no_effect()
    {
        var services = new ServiceCollection();
        services.AddPermissionCatalog(c => c.Add("sims.read"));
        services.AddPermissionCatalog(c => c.Add("sims.read"));

        using var provider = services.BuildServiceProvider();

        Assert.Single(provider.GetRequiredService<PermissionCatalog>().All);
    }

    [Fact]
    public void A_catalog_registered_as_a_factory_cannot_be_added_to()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => new PermissionCatalog());

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddPermissionCatalog(c => c.Add("sims.read")));

        Assert.Contains(nameof(IdentityDirectoryServiceCollectionExtensions.AddPermissionCatalog), ex.Message);
    }
}
