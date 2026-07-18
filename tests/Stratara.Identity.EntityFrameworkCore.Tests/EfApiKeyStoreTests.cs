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
