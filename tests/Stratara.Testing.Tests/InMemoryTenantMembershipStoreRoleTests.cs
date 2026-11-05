using Stratara.Abstractions.Multitenancy;
using Xunit;

namespace Stratara.Testing.Tests;

public class InMemoryTenantMembershipStoreRoleTests
{
    private readonly InMemoryTenantMembershipStore _store = new();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _tenantId = Guid.CreateVersion7();

    private ITenantMembershipStore Store => _store;

    [Fact]
    public async Task Adding_a_role_keeps_the_other_roles_and_the_status()
    {
        await Store.SetMembershipAsync(new TenantMembership(_userId, _tenantId, ["Editor"], MembershipStatus.Pending));

        var changed = await Store.AddRolesAsync(_userId, _tenantId, ["TenantAdmin"]);

        Assert.Equal(["Editor", "TenantAdmin"], changed!.Roles);
        Assert.Equal(MembershipStatus.Pending, changed.Status);
        Assert.Equal(changed, await Store.GetMembershipAsync(_userId, _tenantId));
    }

    [Fact]
    public async Task Adding_a_role_already_held_holds_it_once()
    {
        await Store.SetMembershipAsync(new TenantMembership(_userId, _tenantId, ["Editor"]));

        var changed = await Store.AddRolesAsync(_userId, _tenantId, ["Editor", "Viewer", "Viewer"]);

        Assert.Equal(["Editor", "Viewer"], changed!.Roles);
    }

    [Fact]
    public async Task Removing_a_role_keeps_the_others_and_ignores_one_not_held()
    {
        await Store.SetMembershipAsync(new TenantMembership(_userId, _tenantId, ["Editor", "Viewer", "TenantAdmin"]));

        var changed = await Store.RemoveRolesAsync(_userId, _tenantId, ["Viewer", "Auditor"]);

        Assert.Equal(["Editor", "TenantAdmin"], changed!.Roles);
    }

    [Fact]
    public async Task Changing_roles_without_a_membership_creates_none()
    {
        Assert.Null(await Store.AddRolesAsync(_userId, _tenantId, ["Editor"]));
        Assert.Null(await Store.RemoveRolesAsync(_userId, _tenantId, ["Editor"]));
        Assert.Null(await Store.GetMembershipAsync(_userId, _tenantId));
    }

    [Fact]
    public async Task A_blank_role_name_is_refused()
    {
        await Store.SetMembershipAsync(new TenantMembership(_userId, _tenantId, []));

        await Assert.ThrowsAsync<ArgumentException>(() => Store.AddRolesAsync(_userId, _tenantId, [" "]));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Store.RemoveRolesAsync(_userId, _tenantId, null!));
    }

    [Fact]
    public async Task Concurrent_additions_of_different_roles_all_survive()
    {
        await Store.SetMembershipAsync(new TenantMembership(_userId, _tenantId, []));
        var roles = Enumerable.Range(0, 50).Select(i => $"role-{i}").ToList();

        await Task.WhenAll(roles.Select(role => Task.Run(() => Store.AddRolesAsync(_userId, _tenantId, [role]))));

        Assert.Equal(roles.Order(), (await Store.GetMembershipAsync(_userId, _tenantId))!.Roles.Order());
    }

    [Fact]
    public async Task A_store_that_does_not_override_the_role_operations_gets_them_by_reading_and_writing_back()
    {
        ITenantMembershipStore store = new ReadWriteOnlyStore(_store);
        await store.SetMembershipAsync(new TenantMembership(_userId, _tenantId, ["Editor"]));

        Assert.Equal(["Editor", "Viewer"], (await store.AddRolesAsync(_userId, _tenantId, ["Viewer"]))!.Roles);
        Assert.Equal(["Viewer"], (await store.RemoveRolesAsync(_userId, _tenantId, ["Editor"]))!.Roles);
        Assert.Equal(["Viewer"], (await _store.GetMembershipAsync(_userId, _tenantId))!.Roles);
        Assert.Null(await store.AddRolesAsync(Guid.CreateVersion7(), _tenantId, ["Viewer"]));
    }

    private sealed class ReadWriteOnlyStore(ITenantMembershipStore inner) : ITenantMembershipStore
    {
        public Task<IReadOnlyList<TenantMembership>> GetMembershipsAsync(Guid userId, CancellationToken cancellationToken = default) =>
            inner.GetMembershipsAsync(userId, cancellationToken);

        public Task<TenantMembership?> GetMembershipAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken = default) =>
            inner.GetMembershipAsync(userId, tenantId, cancellationToken);

        public Task<IReadOnlyList<TenantMembership>> GetMembersAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            inner.GetMembersAsync(tenantId, cancellationToken);

        public Task SetMembershipAsync(TenantMembership membership, CancellationToken cancellationToken = default) =>
            inner.SetMembershipAsync(membership, cancellationToken);

        public Task RemoveMembershipAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken = default) =>
            inner.RemoveMembershipAsync(userId, tenantId, cancellationToken);

        public Task RemoveAllMembershipsAsync(Guid userId, CancellationToken cancellationToken = default) =>
            inner.RemoveAllMembershipsAsync(userId, cancellationToken);

        public Task RemoveAllMembersAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            inner.RemoveAllMembersAsync(tenantId, cancellationToken);

        public Task<Guid?> GetActiveTenantAsync(Guid userId, CancellationToken cancellationToken = default) =>
            inner.GetActiveTenantAsync(userId, cancellationToken);

        public Task SetActiveTenantAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken = default) =>
            inner.SetActiveTenantAsync(userId, tenantId, cancellationToken);
    }
}
