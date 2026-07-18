using Microsoft.AspNetCore.Identity;
using Moq;
using Stratara.Abstractions.Multitenancy;
using Stratara.Testing;
using Xunit;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

public class MembershipAuthorizationProviderTests
{
    [Fact]
    public async Task Role_on_active_membership_in_subject_tenant_passes()
    {
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["TenantAdmin"]));

        var provider = new MembershipAuthorizationProvider(
            TestSessionContextProvider.ForTenant(tenantId, userId), store);

        Assert.True(await provider.IsInRoleAsync("TenantAdmin"));
        Assert.False(await provider.IsInRoleAsync("OtherRole"));
    }

    [Fact]
    public async Task Pending_membership_confers_no_roles()
    {
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(
            new TenantMembership(userId, tenantId, ["TenantAdmin"], MembershipStatus.Pending));

        var provider = new MembershipAuthorizationProvider(
            TestSessionContextProvider.ForTenant(tenantId, userId), store);

        Assert.False(await provider.IsInRoleAsync("TenantAdmin"));
    }

    [Fact]
    public async Task Membership_in_another_tenant_does_not_leak_into_the_subject_tenant()
    {
        var subjectTenant = Guid.CreateVersion7();
        var otherTenant = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(new TenantMembership(userId, otherTenant, ["TenantAdmin"]));

        var provider = new MembershipAuthorizationProvider(
            TestSessionContextProvider.ForTenant(subjectTenant, userId), store);

        Assert.False(await provider.IsInRoleAsync("TenantAdmin"));
    }

    [Fact]
    public async Task Missing_session_fails_closed()
    {
        var provider = new MembershipAuthorizationProvider(
            new TestSessionContextProvider(), new InMemoryTenantMembershipStore());

        Assert.False(await provider.IsInRoleAsync("TenantAdmin"));
    }

    [Fact]
    public async Task Global_identity_role_passes_on_membership_miss()
    {
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var user = new IdentityUser { Id = userId.ToString() };
        var userManager = CreateUserManager(user, "PlatformAdmin");

        var provider = new MembershipAuthorizationProvider<IdentityUser>(
            TestSessionContextProvider.ForTenant(tenantId, userId),
            new InMemoryTenantMembershipStore(),
            userManager);

        Assert.True(await provider.IsInRoleAsync("PlatformAdmin"));
        Assert.False(await provider.IsInRoleAsync("Developer"));
    }

    [Fact]
    public async Task Membership_role_passes_without_consulting_the_identity_store()
    {
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["TenantAdmin"]));

        var userManagerMock = CreateUserManagerMock();
        var provider = new MembershipAuthorizationProvider<IdentityUser>(
            TestSessionContextProvider.ForTenant(tenantId, userId), store, userManagerMock.Object);

        Assert.True(await provider.IsInRoleAsync("TenantAdmin"));
        userManagerMock.Verify(m => m.FindByIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Unknown_identity_user_fails_closed()
    {
        var provider = new MembershipAuthorizationProvider<IdentityUser>(
            TestSessionContextProvider.ForTenant(Guid.CreateVersion7(), Guid.CreateVersion7()),
            new InMemoryTenantMembershipStore(),
            CreateUserManagerMock().Object);

        Assert.False(await provider.IsInRoleAsync("PlatformAdmin"));
    }

    private static UserManager<IdentityUser> CreateUserManager(IdentityUser user, params string[] globalRoles)
    {
        var mock = CreateUserManagerMock();
        mock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        mock.Setup(m => m.IsInRoleAsync(user, It.IsAny<string>()))
            .ReturnsAsync((IdentityUser _, string role) => globalRoles.Contains(role, StringComparer.Ordinal));
        return mock.Object;
    }

    private static Mock<UserManager<IdentityUser>> CreateUserManagerMock() =>
        new(Mock.Of<IUserStore<IdentityUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);
}
