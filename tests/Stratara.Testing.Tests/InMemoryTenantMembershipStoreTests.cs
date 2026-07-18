using Stratara.Abstractions.Multitenancy;
using Xunit;

namespace Stratara.Testing.Tests;

public class InMemoryTenantMembershipStoreTests
{
    [Fact]
    public async Task Set_then_get_roundtrips_and_upserts()
    {
        var store = new InMemoryTenantMembershipStore();
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["Editor"]));
        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["TenantAdmin"]));

        var membership = await store.GetMembershipAsync(userId, tenantId);
        Assert.NotNull(membership);
        Assert.Equal(["TenantAdmin"], membership.Roles);
        Assert.Single(await store.GetMembershipsAsync(userId));
    }

    [Fact]
    public async Task Forward_and_reverse_lookups_agree()
    {
        var store = new InMemoryTenantMembershipStore();
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, []));
        await store.SetMembershipAsync(new TenantMembership(Guid.CreateVersion7(), tenantId, []));

        Assert.Single(await store.GetMembershipsAsync(userId));
        Assert.Equal(2, (await store.GetMembersAsync(tenantId)).Count);
    }

    [Fact]
    public async Task Sweeps_clear_memberships_and_selections()
    {
        var store = new InMemoryTenantMembershipStore();
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, []));
        await store.SetActiveTenantAsync(userId, tenantId);

        await store.RemoveAllMembersAsync(tenantId);

        Assert.Empty(await store.GetMembersAsync(tenantId));
        Assert.Null(await store.GetActiveTenantAsync(userId));

        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, []));
        await store.SetActiveTenantAsync(userId, tenantId);
        await store.RemoveAllMembershipsAsync(userId);

        Assert.Empty(await store.GetMembershipsAsync(userId));
        Assert.Null(await store.GetActiveTenantAsync(userId));
    }

    [Fact]
    public async Task Active_tenant_selection_is_membership_guarded()
    {
        var store = new InMemoryTenantMembershipStore();
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetActiveTenantAsync(userId, tenantId));

        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, [], MembershipStatus.Pending));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetActiveTenantAsync(userId, tenantId));

        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, []));
        await store.SetActiveTenantAsync(userId, tenantId);
        Assert.Equal(tenantId, await store.GetActiveTenantAsync(userId));

        await store.RemoveMembershipAsync(userId, tenantId);
        Assert.Null(await store.GetActiveTenantAsync(userId));
    }
}
