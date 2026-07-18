using Microsoft.AspNetCore.Identity;
using Moq;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Multitenancy;
using Stratara.Testing;
using Xunit;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

public class CatalogPermissionResolverTests
{
    private static PermissionCatalog Catalog()
    {
        var catalog = new PermissionCatalog();
        catalog.Add("sims.read", "sims.write", "billing.read");
        catalog.GrantToRole("TenantAdmin", "sims.read", "sims.write");
        catalog.GrantToRole("Support", "sims.read");
        catalog.GrantToRole("PlatformAdmin", "billing.read");
        return catalog;
    }

    [Fact]
    public async Task Membership_roles_map_through_the_catalog_grants()
    {
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["TenantAdmin"]));

        var resolver = new CatalogPermissionResolver(store, Catalog());
        var permissions = await resolver.ResolvePermissionsAsync(userId, tenantId);

        Assert.Equal(2, permissions.Count);
        Assert.Contains("sims.read", permissions);
        Assert.Contains("sims.write", permissions);
        Assert.DoesNotContain("billing.read", permissions);
    }

    [Fact]
    public async Task No_membership_or_pending_membership_yields_nothing()
    {
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        var store = new InMemoryTenantMembershipStore();

        var resolver = new CatalogPermissionResolver(store, Catalog());
        Assert.Empty(await resolver.ResolvePermissionsAsync(userId, tenantId));

        await store.SetMembershipAsync(
            new TenantMembership(userId, tenantId, ["TenantAdmin"], MembershipStatus.Pending));
        Assert.Empty(await resolver.ResolvePermissionsAsync(userId, Guid.CreateVersion7()));
    }

    [Fact]
    public async Task Resolution_is_memoized_per_scope()
    {
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        var storeMock = new Mock<ITenantMembershipStore>();
        storeMock
            .Setup(s => s.GetMembershipAsync(userId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantMembership(userId, tenantId, ["Support"]));

        var resolver = new CatalogPermissionResolver(storeMock.Object, Catalog());
        await resolver.ResolvePermissionsAsync(userId, tenantId);
        await resolver.ResolvePermissionsAsync(userId, tenantId);

        storeMock.Verify(
            s => s.GetMembershipAsync(userId, tenantId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Global_identity_roles_also_grant_permissions()
    {
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        var user = new IdentityUser { Id = userId.ToString() };

        var userManagerMock = new Mock<UserManager<IdentityUser>>(
            Mock.Of<IUserStore<IdentityUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);
        userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        userManagerMock.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(["PlatformAdmin"]);

        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["Support"]));

        var resolver = new CatalogPermissionResolver<IdentityUser>(store, userManagerMock.Object, Catalog());
        var permissions = await resolver.ResolvePermissionsAsync(userId, tenantId);

        Assert.Contains("sims.read", permissions);     // via membership role Support
        Assert.Contains("billing.read", permissions);  // via global role PlatformAdmin
        Assert.DoesNotContain("sims.write", permissions);
    }

    [Fact]
    public async Task Unknown_identity_user_contributes_no_global_roles()
    {
        var userManagerMock = new Mock<UserManager<IdentityUser>>(
            Mock.Of<IUserStore<IdentityUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);

        var resolver = new CatalogPermissionResolver<IdentityUser>(
            new InMemoryTenantMembershipStore(), userManagerMock.Object, Catalog());

        Assert.Empty(await resolver.ResolvePermissionsAsync(Guid.CreateVersion7(), Guid.CreateVersion7()));
    }
}
