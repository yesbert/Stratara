using Stratara.Abstractions.Multitenancy;
using Xunit;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

public class EfTenantMembershipStoreTests
{
    [Fact]
    public async Task Set_then_get_roundtrips_roles_and_status()
    {
        using var fixture = new SqliteDirectoryFixture();
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        await fixture.Store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["TenantAdmin", "Editor"]));

        var membership = await fixture.Store.GetMembershipAsync(userId, tenantId);
        Assert.NotNull(membership);
        Assert.Equal(userId, membership.UserId);
        Assert.Equal(tenantId, membership.TenantId);
        Assert.Equal(["TenantAdmin", "Editor"], membership.Roles);
        Assert.Equal(MembershipStatus.Active, membership.Status);
    }

    [Fact]
    public async Task Set_is_an_upsert_replacing_roles_and_status()
    {
        using var fixture = new SqliteDirectoryFixture();
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        await fixture.Store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["Editor"]));
        await fixture.Store.SetMembershipAsync(
            new TenantMembership(userId, tenantId, ["TenantAdmin"], MembershipStatus.Pending));

        var membership = await fixture.Store.GetMembershipAsync(userId, tenantId);
        Assert.NotNull(membership);
        Assert.Equal(["TenantAdmin"], membership.Roles);
        Assert.Equal(MembershipStatus.Pending, membership.Status);
        Assert.Single(await fixture.Store.GetMembershipsAsync(userId));
    }

    [Fact]
    public async Task Forward_and_reverse_lookups_see_the_same_rows()
    {
        using var fixture = new SqliteDirectoryFixture();
        var userA = Guid.CreateVersion7();
        var userB = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        var otherTenant = Guid.CreateVersion7();

        await fixture.Store.SetMembershipAsync(new TenantMembership(userA, tenantId, []));
        await fixture.Store.SetMembershipAsync(new TenantMembership(userB, tenantId, []));
        await fixture.Store.SetMembershipAsync(new TenantMembership(userA, otherTenant, []));

        Assert.Equal(2, (await fixture.Store.GetMembershipsAsync(userA)).Count);
        var members = await fixture.Store.GetMembersAsync(tenantId);
        Assert.Equal(2, members.Count);
        Assert.Contains(members, m => m.UserId == userA);
        Assert.Contains(members, m => m.UserId == userB);
    }

    [Fact]
    public async Task Missing_membership_returns_null_and_empty_lists()
    {
        using var fixture = new SqliteDirectoryFixture();

        Assert.Null(await fixture.Store.GetMembershipAsync(Guid.CreateVersion7(), Guid.CreateVersion7()));
        Assert.Empty(await fixture.Store.GetMembershipsAsync(Guid.CreateVersion7()));
        Assert.Empty(await fixture.Store.GetMembersAsync(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task RemoveAllMemberships_sweeps_the_user_and_their_selection()
    {
        using var fixture = new SqliteDirectoryFixture();
        var userId = Guid.CreateVersion7();
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();

        await fixture.Store.SetMembershipAsync(new TenantMembership(userId, tenantA, []));
        await fixture.Store.SetMembershipAsync(new TenantMembership(userId, tenantB, []));
        await fixture.Store.SetActiveTenantAsync(userId, tenantB);

        await fixture.Store.RemoveAllMembershipsAsync(userId);

        Assert.Empty(await fixture.Store.GetMembershipsAsync(userId));
        Assert.Null(await fixture.Store.GetActiveTenantAsync(userId));
    }

    [Fact]
    public async Task RemoveAllMembers_sweeps_the_tenant_and_dangling_selections()
    {
        using var fixture = new SqliteDirectoryFixture();
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        var untouchedTenant = Guid.CreateVersion7();

        await fixture.Store.SetMembershipAsync(new TenantMembership(userId, tenantId, []));
        await fixture.Store.SetMembershipAsync(new TenantMembership(userId, untouchedTenant, []));
        await fixture.Store.SetActiveTenantAsync(userId, tenantId);

        await fixture.Store.RemoveAllMembersAsync(tenantId);

        Assert.Empty(await fixture.Store.GetMembersAsync(tenantId));
        Assert.NotNull(await fixture.Store.GetMembershipAsync(userId, untouchedTenant));
        Assert.Null(await fixture.Store.GetActiveTenantAsync(userId));
    }

    [Fact]
    public async Task Removing_the_selected_membership_clears_the_selection()
    {
        using var fixture = new SqliteDirectoryFixture();
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        await fixture.Store.SetMembershipAsync(new TenantMembership(userId, tenantId, []));
        await fixture.Store.SetActiveTenantAsync(userId, tenantId);

        await fixture.Store.RemoveMembershipAsync(userId, tenantId);

        Assert.Null(await fixture.Store.GetActiveTenantAsync(userId));
    }

    [Fact]
    public async Task Active_tenant_selection_requires_an_active_membership()
    {
        using var fixture = new SqliteDirectoryFixture();
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Store.SetActiveTenantAsync(userId, tenantId));

        await fixture.Store.SetMembershipAsync(
            new TenantMembership(userId, tenantId, [], MembershipStatus.Pending));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Store.SetActiveTenantAsync(userId, tenantId));

        await fixture.Store.SetMembershipAsync(new TenantMembership(userId, tenantId, []));
        await fixture.Store.SetActiveTenantAsync(userId, tenantId);

        Assert.Equal(tenantId, await fixture.Store.GetActiveTenantAsync(userId));
    }

    [Fact]
    public async Task Switching_the_selection_updates_the_single_row()
    {
        using var fixture = new SqliteDirectoryFixture();
        var userId = Guid.CreateVersion7();
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();

        await fixture.Store.SetMembershipAsync(new TenantMembership(userId, tenantA, []));
        await fixture.Store.SetMembershipAsync(new TenantMembership(userId, tenantB, []));

        await fixture.Store.SetActiveTenantAsync(userId, tenantA);
        await fixture.Store.SetActiveTenantAsync(userId, tenantB);

        Assert.Equal(tenantB, await fixture.Store.GetActiveTenantAsync(userId));
    }
}
