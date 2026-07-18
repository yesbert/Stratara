using Stratara.Abstractions.Authorization;

namespace Stratara.Infrastructure.Tests.Authorization;

public class PermissionCatalogTests
{
    [Fact]
    public void Declared_permissions_are_listed_and_queryable()
    {
        var catalog = new PermissionCatalog();
        catalog.Add("sims.read", "sims.write");

        Assert.Equal(2, catalog.All.Count);
        Assert.True(catalog.Contains("sims.read"));
        Assert.False(catalog.Contains("billing.read"));
    }

    [Fact]
    public void Redeclaring_a_permission_is_a_noop()
    {
        var catalog = new PermissionCatalog();
        catalog.Add("sims.read");
        catalog.Add("sims.read");

        Assert.Single(catalog.All);
    }

    [Fact]
    public void Role_grants_accumulate_across_calls()
    {
        var catalog = new PermissionCatalog();
        catalog.Add("sims.read", "sims.write", "billing.read");
        catalog.GrantToRole("TenantAdmin", "sims.read");
        catalog.GrantToRole("TenantAdmin", "sims.write");

        var grants = catalog.GetRolePermissions("TenantAdmin");
        Assert.Equal(2, grants.Count);
        Assert.Contains("sims.read", grants);
        Assert.Contains("sims.write", grants);
        Assert.Empty(catalog.GetRolePermissions("UnknownRole"));
    }

    [Fact]
    public void Granting_an_undeclared_permission_throws()
    {
        var catalog = new PermissionCatalog();
        catalog.Add("sims.read");

        var ex = Assert.Throws<ArgumentException>(() => catalog.GrantToRole("TenantAdmin", "sims.write"));
        Assert.Contains("sims.write", ex.Message);
    }

    [Fact]
    public void Empty_or_whitespace_names_are_rejected()
    {
        var catalog = new PermissionCatalog();

        Assert.Throws<ArgumentException>(() => catalog.Add(" "));
        Assert.Throws<ArgumentException>(() => catalog.GrantToRole("", "x"));
    }
}
