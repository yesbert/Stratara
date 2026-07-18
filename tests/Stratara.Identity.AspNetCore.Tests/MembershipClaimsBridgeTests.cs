using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Stratara.Abstractions.Multitenancy;
using Stratara.Identity.AspNetCore.Services;
using Stratara.Sessions.Multitenancy;
using Stratara.Testing;
using Xunit;

namespace Stratara.Identity.AspNetCore.Tests;

public class MembershipClaimsBridgeTests
{
    private sealed class PassThroughFactory(Func<IdentityUser, ClaimsPrincipal> create)
        : IUserClaimsPrincipalFactory<IdentityUser>
    {
        public Task<ClaimsPrincipal> CreateAsync(IdentityUser user) => Task.FromResult(create(user));
    }

    private static ClaimsPrincipal AuthenticatedPrincipal(Guid userId, params Claim[] extraClaims)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        claims.AddRange(extraClaims);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private static string? TenantClaim(ClaimsPrincipal principal) =>
        principal.FindFirstValue(StrataraClaimTypes.TenantId);

    [Fact]
    public async Task Factory_stamps_the_single_active_membership()
    {
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, []));

        var factory = new MembershipClaimsPrincipalFactory<IdentityUser>(
            new PassThroughFactory(_ => AuthenticatedPrincipal(userId)), store);

        var principal = await factory.CreateAsync(new IdentityUser { Id = userId.ToString() });

        Assert.Equal(tenantId.ToString("D"), TenantClaim(principal));
    }

    [Fact]
    public async Task Factory_prefers_the_persisted_active_tenant_selection()
    {
        var userId = Guid.CreateVersion7();
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(new TenantMembership(userId, tenantA, []));
        await store.SetMembershipAsync(new TenantMembership(userId, tenantB, []));
        await store.SetActiveTenantAsync(userId, tenantB);

        var factory = new MembershipClaimsPrincipalFactory<IdentityUser>(
            new PassThroughFactory(_ => AuthenticatedPrincipal(userId)), store);

        var principal = await factory.CreateAsync(new IdentityUser { Id = userId.ToString() });

        Assert.Equal(tenantB.ToString("D"), TenantClaim(principal));
    }

    [Fact]
    public async Task Factory_falls_back_to_the_deterministically_first_membership()
    {
        var userId = Guid.CreateVersion7();
        var tenants = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        var store = new InMemoryTenantMembershipStore();
        foreach (var tenant in tenants)
        {
            await store.SetMembershipAsync(new TenantMembership(userId, tenant, []));
        }

        var factory = new MembershipClaimsPrincipalFactory<IdentityUser>(
            new PassThroughFactory(_ => AuthenticatedPrincipal(userId)), store);

        var principal = await factory.CreateAsync(new IdentityUser { Id = userId.ToString() });

        Assert.Equal(tenants.Min().ToString("D"), TenantClaim(principal));
    }

    [Fact]
    public async Task Factory_leaves_a_preexisting_tenant_claim_untouched()
    {
        var userId = Guid.CreateVersion7();
        var stampedTenant = Guid.CreateVersion7();
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(new TenantMembership(userId, Guid.CreateVersion7(), []));

        var factory = new MembershipClaimsPrincipalFactory<IdentityUser>(
            new PassThroughFactory(_ => AuthenticatedPrincipal(
                userId, new Claim(StrataraClaimTypes.TenantId, stampedTenant.ToString("D")))), store);

        var principal = await factory.CreateAsync(new IdentityUser { Id = userId.ToString() });

        Assert.Equal(stampedTenant.ToString("D"), TenantClaim(principal));
        Assert.Single(principal.FindAll(StrataraClaimTypes.TenantId));
    }

    [Fact]
    public async Task Factory_stamps_nothing_without_an_active_membership()
    {
        var userId = Guid.CreateVersion7();
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(
            new TenantMembership(userId, Guid.CreateVersion7(), [], MembershipStatus.Pending));

        var factory = new MembershipClaimsPrincipalFactory<IdentityUser>(
            new PassThroughFactory(_ => AuthenticatedPrincipal(userId)), store);

        var principal = await factory.CreateAsync(new IdentityUser { Id = userId.ToString() });

        Assert.Null(TenantClaim(principal));
    }

    [Fact]
    public async Task Transformation_adds_the_claim_to_an_authenticated_principal()
    {
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, []));

        var transformation = new MembershipClaimsTransformation(store);
        var transformed = await transformation.TransformAsync(AuthenticatedPrincipal(userId));

        Assert.Equal(tenantId.ToString("D"), TenantClaim(transformed));
    }

    [Fact]
    public async Task Transformation_is_idempotent_across_runs()
    {
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, []));

        var transformation = new MembershipClaimsTransformation(store);
        var once = await transformation.TransformAsync(AuthenticatedPrincipal(userId));
        var twice = await transformation.TransformAsync(once);

        Assert.Same(once, twice);
        Assert.Single(twice.FindAll(StrataraClaimTypes.TenantId));
    }

    [Fact]
    public async Task Transformation_skips_unauthenticated_principals()
    {
        var transformation = new MembershipClaimsTransformation(new InMemoryTenantMembershipStore());
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var transformed = await transformation.TransformAsync(anonymous);

        Assert.Same(anonymous, transformed);
        Assert.Null(TenantClaim(transformed));
    }
}
