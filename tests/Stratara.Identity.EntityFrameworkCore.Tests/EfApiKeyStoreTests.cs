using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.ApiKeys;
using Stratara.Abstractions.Multitenancy;
using Xunit;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

public class EfApiKeyStoreTests
{
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class ApiKeyFixture : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;

        public ApiKeyFixture()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var services = new ServiceCollection();
            services.AddDbContext<TestDirectoryDbContext>(o => o.UseSqlite(_connection));
            services.AddSingleton<TimeProvider>(Clock);
            services.AddTenantMembershipStore<TestDirectoryDbContext>();
            services.AddApiKeyStore<TestDirectoryDbContext>();
            _provider = services.BuildServiceProvider();

            _scope = _provider.CreateScope();
            _scope.ServiceProvider.GetRequiredService<TestDirectoryDbContext>().Database.EnsureCreated();
            Store = _scope.ServiceProvider.GetRequiredService<IApiKeyStore>();
            Memberships = _scope.ServiceProvider.GetRequiredService<ITenantMembershipStore>();
        }

        public TestClock Clock { get; } = new(DateTimeOffset.Parse("2026-07-09T03:00:00Z"));

        public IApiKeyStore Store { get; }

        public ITenantMembershipStore Memberships { get; }

        public void Dispose()
        {
            _scope.Dispose();
            _provider.Dispose();
            _connection.Dispose();
        }
    }

    [Fact]
    public async Task Issued_machine_key_validates_and_carries_its_descriptor()
    {
        using var fixture = new ApiKeyFixture();
        var tenantId = Guid.CreateVersion7();

        var issued = await fixture.Store.IssueAsync(new ApiKeyIssueRequest(tenantId, "CI key", ["Deployer"]));

        Assert.StartsWith("stk_", issued.RawKey);
        var descriptor = await fixture.Store.ValidateAsync(issued.RawKey);
        Assert.NotNull(descriptor);
        Assert.Equal(issued.Descriptor.Id, descriptor.Id);
        Assert.Equal(tenantId, descriptor.TenantId);
        Assert.Null(descriptor.UserId);
        Assert.Equal(["Deployer"], descriptor.Roles);
    }

    [Fact]
    public async Task Machine_key_issuance_materializes_a_membership_with_the_key_roles()
    {
        using var fixture = new ApiKeyFixture();
        var tenantId = Guid.CreateVersion7();

        var issued = await fixture.Store.IssueAsync(new ApiKeyIssueRequest(tenantId, "CI key", ["Deployer"]));

        var membership = await fixture.Memberships.GetMembershipAsync(issued.Descriptor.Id, tenantId);
        Assert.NotNull(membership);
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.Equal(["Deployer"], membership.Roles);
    }

    [Fact]
    public async Task Unknown_and_garbage_keys_fail_closed()
    {
        using var fixture = new ApiKeyFixture();

        Assert.Null(await fixture.Store.ValidateAsync("stk_definitely-not-issued"));
        Assert.Null(await fixture.Store.ValidateAsync(""));
        Assert.Null(await fixture.Store.ValidateAsync("   "));
    }

    [Fact]
    public async Task Revocation_stops_validation_and_removes_the_machine_membership()
    {
        using var fixture = new ApiKeyFixture();
        var tenantId = Guid.CreateVersion7();
        var issued = await fixture.Store.IssueAsync(new ApiKeyIssueRequest(tenantId, "CI key", ["Deployer"]));

        await fixture.Store.RevokeAsync(issued.Descriptor.Id);

        Assert.Null(await fixture.Store.ValidateAsync(issued.RawKey));
        Assert.Null(await fixture.Memberships.GetMembershipAsync(issued.Descriptor.Id, tenantId));
        var listed = await fixture.Store.GetForTenantAsync(tenantId);
        Assert.NotNull(Assert.Single(listed).RevokedAt);
    }

    [Fact]
    public async Task Expiry_is_enforced_at_validation_time()
    {
        using var fixture = new ApiKeyFixture();
        var tenantId = Guid.CreateVersion7();
        var issued = await fixture.Store.IssueAsync(new ApiKeyIssueRequest(
            tenantId, "short-lived", [], ExpiresAt: fixture.Clock.GetUtcNow().AddHours(1)));

        Assert.NotNull(await fixture.Store.ValidateAsync(issued.RawKey));

        fixture.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Null(await fixture.Store.ValidateAsync(issued.RawKey));
    }

    [Fact]
    public async Task Personal_access_token_requires_an_active_membership_and_no_roles()
    {
        using var fixture = new ApiKeyFixture();
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.IssueAsync(new ApiKeyIssueRequest(tenantId, "pat", [], userId)));

        await fixture.Memberships.SetMembershipAsync(new TenantMembership(userId, tenantId, []));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.IssueAsync(new ApiKeyIssueRequest(tenantId, "pat", ["Sneaky"], userId)));

        var issued = await fixture.Store.IssueAsync(new ApiKeyIssueRequest(tenantId, "pat", [], userId));
        var descriptor = await fixture.Store.ValidateAsync(issued.RawKey);
        Assert.NotNull(descriptor);
        Assert.Equal(userId, descriptor.UserId);
        Assert.Null(await fixture.Memberships.GetMembershipAsync(issued.Descriptor.Id, tenantId));
    }

    [Fact]
    public async Task Imported_key_validates_and_materializes_its_membership()
    {
        using var fixture = new ApiKeyFixture();
        var tenantId = Guid.CreateVersion7();
        var rawKey = ApiKeyFormat.CreateRawKey();

        var descriptor = await fixture.Store.ImportAsync(
            new ApiKeyImportRequest(rawKey, tenantId, "bootstrap", ["Admin"]));

        var validated = await fixture.Store.ValidateAsync(rawKey);
        Assert.NotNull(validated);
        Assert.Equal(descriptor.Id, validated.Id);
        Assert.Null(validated.UserId);

        var membership = await fixture.Memberships.GetMembershipAsync(descriptor.Id, tenantId);
        Assert.NotNull(membership);
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.Equal(["Admin"], membership.Roles);
    }

    [Fact]
    public async Task Repeated_import_is_a_no_op_that_never_mutates_the_stored_key()
    {
        using var fixture = new ApiKeyFixture();
        var tenantId = Guid.CreateVersion7();
        var rawKey = ApiKeyFormat.CreateRawKey();
        var first = await fixture.Store.ImportAsync(
            new ApiKeyImportRequest(rawKey, tenantId, "bootstrap", ["Viewer"]));

        var second = await fixture.Store.ImportAsync(
            new ApiKeyImportRequest(rawKey, tenantId, "renamed", ["Admin"]));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("bootstrap", second.Name);
        Assert.Equal(["Viewer"], second.Roles);
        Assert.Single(await fixture.Store.GetForTenantAsync(tenantId));

        var membership = await fixture.Memberships.GetMembershipAsync(first.Id, tenantId);
        Assert.NotNull(membership);
        Assert.Equal(["Viewer"], membership.Roles);
    }

    [Fact]
    public async Task Import_rejects_values_outside_the_canonical_format()
    {
        using var fixture = new ApiKeyFixture();
        var wellFormed = ApiKeyFormat.CreateRawKey();
        string[] rejected =
        [
            "hunter2",
            "stk_short",
            wellFormed[..^1],               // one character too short
            wellFormed + "a",               // one character too long
            "pat_" + wellFormed[4..],       // wrong prefix
            wellFormed[..^1] + "!",         // outside the Base64Url alphabet
        ];

        foreach (var rawKey in rejected)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.ImportAsync(
                new ApiKeyImportRequest(rawKey, Guid.CreateVersion7(), "bootstrap", [])));
        }
    }

    [Fact]
    public async Task Import_refuses_to_rebind_a_known_key_to_another_tenant()
    {
        using var fixture = new ApiKeyFixture();
        var rawKey = ApiKeyFormat.CreateRawKey();
        await fixture.Store.ImportAsync(new ApiKeyImportRequest(rawKey, Guid.CreateVersion7(), "bootstrap", []));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ImportAsync(
            new ApiKeyImportRequest(rawKey, Guid.CreateVersion7(), "bootstrap", [])));
    }

    [Fact]
    public async Task Import_never_reinstates_a_revoked_key()
    {
        using var fixture = new ApiKeyFixture();
        var tenantId = Guid.CreateVersion7();
        var rawKey = ApiKeyFormat.CreateRawKey();
        var descriptor = await fixture.Store.ImportAsync(
            new ApiKeyImportRequest(rawKey, tenantId, "bootstrap", []));
        await fixture.Store.RevokeAsync(descriptor.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ImportAsync(
            new ApiKeyImportRequest(rawKey, tenantId, "bootstrap", [])));
        Assert.Null(await fixture.Store.ValidateAsync(rawKey));
    }

    [Fact]
    public async Task Import_never_extends_an_expired_key()
    {
        using var fixture = new ApiKeyFixture();
        var tenantId = Guid.CreateVersion7();
        var rawKey = ApiKeyFormat.CreateRawKey();
        await fixture.Store.ImportAsync(new ApiKeyImportRequest(
            rawKey, tenantId, "bootstrap", [], fixture.Clock.GetUtcNow().AddHours(1)));

        fixture.Clock.Advance(TimeSpan.FromHours(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ImportAsync(
            new ApiKeyImportRequest(rawKey, tenantId, "bootstrap", [], fixture.Clock.GetUtcNow().AddHours(1))));
    }

    [Fact]
    public async Task Import_refuses_a_value_already_stored_as_a_personal_access_token()
    {
        using var fixture = new ApiKeyFixture();
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        await fixture.Memberships.SetMembershipAsync(new TenantMembership(userId, tenantId, []));
        var pat = await fixture.Store.IssueAsync(new ApiKeyIssueRequest(tenantId, "pat", [], userId));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ImportAsync(
            new ApiKeyImportRequest(pat.RawKey, tenantId, "bootstrap", [])));
    }

    [Fact]
    public async Task Repeated_import_restores_a_machine_membership_that_went_missing()
    {
        using var fixture = new ApiKeyFixture();
        var tenantId = Guid.CreateVersion7();
        var rawKey = ApiKeyFormat.CreateRawKey();
        var descriptor = await fixture.Store.ImportAsync(
            new ApiKeyImportRequest(rawKey, tenantId, "bootstrap", ["Admin"]));
        await fixture.Memberships.RemoveMembershipAsync(descriptor.Id, tenantId);

        await fixture.Store.ImportAsync(new ApiKeyImportRequest(rawKey, tenantId, "bootstrap", ["Admin"]));

        var membership = await fixture.Memberships.GetMembershipAsync(descriptor.Id, tenantId);
        Assert.NotNull(membership);
        Assert.Equal(["Admin"], membership.Roles);
    }

    [Fact]
    public async Task Tenant_sweep_removes_keys_and_machine_memberships()
    {
        using var fixture = new ApiKeyFixture();
        var tenantId = Guid.CreateVersion7();
        var otherTenant = Guid.CreateVersion7();
        var machine = await fixture.Store.IssueAsync(new ApiKeyIssueRequest(tenantId, "machine", ["Deployer"]));
        var untouched = await fixture.Store.IssueAsync(new ApiKeyIssueRequest(otherTenant, "other", []));

        await fixture.Store.RemoveAllForTenantAsync(tenantId);

        Assert.Empty(await fixture.Store.GetForTenantAsync(tenantId));
        Assert.Null(await fixture.Memberships.GetMembershipAsync(machine.Descriptor.Id, tenantId));
        Assert.NotNull(await fixture.Store.ValidateAsync(untouched.RawKey));
    }

    [Fact]
    public async Task User_sweep_removes_only_the_users_personal_access_tokens()
    {
        using var fixture = new ApiKeyFixture();
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        await fixture.Memberships.SetMembershipAsync(new TenantMembership(userId, tenantId, []));

        var pat = await fixture.Store.IssueAsync(new ApiKeyIssueRequest(tenantId, "pat", [], userId));
        var machine = await fixture.Store.IssueAsync(new ApiKeyIssueRequest(tenantId, "machine", []));

        await fixture.Store.RemoveAllForUserAsync(userId);

        Assert.Null(await fixture.Store.ValidateAsync(pat.RawKey));
        Assert.NotNull(await fixture.Store.ValidateAsync(machine.RawKey));
    }
}
