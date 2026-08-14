using Stratara.Abstractions.ApiKeys;
using Stratara.Abstractions.Multitenancy;
using Stratara.Testing;
using Xunit;

namespace Stratara.Testing.Tests;

public class InMemoryApiKeyStoreTests
{
    private static readonly Guid TenantId = TestTenants.Of("primary");

    [Fact]
    public async Task Issued_machine_key_validates_and_materializes_its_membership()
    {
        var store = new InMemoryApiKeyStore();

        var issued = await store.IssueAsync(new ApiKeyIssueRequest(TenantId, "ci", ["Deployer"]));

        Assert.True(ApiKeyFormat.IsWellFormed(issued.RawKey));
        Assert.NotNull(await store.ValidateAsync(issued.RawKey));

        var membership = await store.Memberships.GetMembershipAsync(issued.Descriptor.Id, TenantId);
        Assert.NotNull(membership);
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.Equal(["Deployer"], membership.Roles);
    }

    [Fact]
    public async Task Imported_key_validates_and_repeats_as_a_no_op()
    {
        var store = new InMemoryApiKeyStore();
        var rawKey = ApiKeyFormat.CreateRawKey();

        var first = await store.ImportAsync(new ApiKeyImportRequest(rawKey, TenantId, "bootstrap", ["Admin"]));
        var second = await store.ImportAsync(new ApiKeyImportRequest(rawKey, TenantId, "renamed", ["Viewer"]));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("bootstrap", second.Name);
        Assert.Equal(["Admin"], second.Roles);
        Assert.Single(await store.GetForTenantAsync(TenantId));
        Assert.NotNull(await store.ValidateAsync(rawKey));
    }

    [Fact]
    public async Task Import_rejects_values_outside_the_canonical_format()
    {
        var store = new InMemoryApiKeyStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.ImportAsync(
            new ApiKeyImportRequest("hunter2", TenantId, "bootstrap", [])));
    }

    [Fact]
    public async Task Import_refuses_a_known_key_for_another_tenant_and_after_revocation()
    {
        var store = new InMemoryApiKeyStore();
        var rawKey = ApiKeyFormat.CreateRawKey();
        var descriptor = await store.ImportAsync(new ApiKeyImportRequest(rawKey, TenantId, "bootstrap", []));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportAsync(
            new ApiKeyImportRequest(rawKey, TestTenants.Of("secondary"), "bootstrap", [])));

        await store.RevokeAsync(descriptor.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportAsync(
            new ApiKeyImportRequest(rawKey, TenantId, "bootstrap", [])));
    }

    [Fact]
    public async Task Revocation_stops_validation_and_removes_the_machine_membership()
    {
        var store = new InMemoryApiKeyStore();
        var issued = await store.IssueAsync(new ApiKeyIssueRequest(TenantId, "ci", ["Deployer"]));

        await store.RevokeAsync(issued.Descriptor.Id);

        Assert.Null(await store.ValidateAsync(issued.RawKey));
        Assert.Null(await store.Memberships.GetMembershipAsync(issued.Descriptor.Id, TenantId));
        Assert.NotNull(Assert.Single(await store.GetForTenantAsync(TenantId)).RevokedAt);
    }

    [Fact]
    public async Task Personal_access_token_requires_an_active_membership_and_no_roles()
    {
        var memberships = new InMemoryTenantMembershipStore();
        var store = new InMemoryApiKeyStore(memberships);
        var userId = Guid.CreateVersion7();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.IssueAsync(new ApiKeyIssueRequest(TenantId, "cli", [], userId)));

        await memberships.SetMembershipAsync(new TenantMembership(userId, TenantId, []));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.IssueAsync(new ApiKeyIssueRequest(TenantId, "cli", ["Sneaky"], userId)));

        var issued = await store.IssueAsync(new ApiKeyIssueRequest(TenantId, "cli", [], userId));
        var descriptor = await store.ValidateAsync(issued.RawKey);
        Assert.NotNull(descriptor);
        Assert.Equal(userId, descriptor.UserId);
        Assert.Null(await memberships.GetMembershipAsync(issued.Descriptor.Id, TenantId));
    }

    [Fact]
    public async Task Expiry_is_enforced_at_validation_time()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-14T09:00:00Z"));
        var store = new InMemoryApiKeyStore(clock: clock);
        var issued = await store.IssueAsync(new ApiKeyIssueRequest(
            TenantId, "short-lived", [], ExpiresAt: clock.GetUtcNow().AddHours(1)));

        Assert.NotNull(await store.ValidateAsync(issued.RawKey));

        clock.Advance(TimeSpan.FromHours(2));
        Assert.Null(await store.ValidateAsync(issued.RawKey));
    }

    [Fact]
    public async Task Sweeps_clear_the_key_plane_per_tenant_and_per_user()
    {
        var memberships = new InMemoryTenantMembershipStore();
        var store = new InMemoryApiKeyStore(memberships);
        var userId = Guid.CreateVersion7();
        await memberships.SetMembershipAsync(new TenantMembership(userId, TenantId, []));
        var pat = await store.IssueAsync(new ApiKeyIssueRequest(TenantId, "cli", [], userId));
        var machine = await store.IssueAsync(new ApiKeyIssueRequest(TenantId, "ci", ["Deployer"]));
        var untouched = await store.IssueAsync(new ApiKeyIssueRequest(TestTenants.Of("secondary"), "other", []));

        await store.RemoveAllForUserAsync(userId);
        Assert.Null(await store.ValidateAsync(pat.RawKey));
        Assert.NotNull(await store.ValidateAsync(machine.RawKey));

        await store.RemoveAllForTenantAsync(TenantId);
        Assert.Empty(await store.GetForTenantAsync(TenantId));
        Assert.Null(await memberships.GetMembershipAsync(machine.Descriptor.Id, TenantId));
        Assert.NotNull(await store.ValidateAsync(untouched.RawKey));
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
