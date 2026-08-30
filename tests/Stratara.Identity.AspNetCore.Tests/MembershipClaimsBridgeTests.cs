using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;
using Stratara.Identity.AspNetCore.Services;
using Stratara.Sessions.Middlewares;
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

    private sealed class RecordingSessionContextProvider : ISessionContextProvider
    {
        public SessionContext? Current { get; private set; }

        public void Clear() => Current = null;

        public void Set(SessionContext context) => Current = context;
    }

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

    /// <summary>
    /// The assertion the old coverage was missing. It tested that resolution was *deterministic*,
    /// which a Guid sort satisfies while still handing the user a tenant nobody chose.
    /// </summary>
    [Fact]
    public async Task Factory_stamps_nothing_when_several_memberships_and_no_selection()
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

        Assert.Null(TenantClaim(principal));
    }

    /// <summary>
    /// The path most likely to be reintroduced by someone reasoning "they had a selection, so pick
    /// something close to it". A stale selection is not a weaker signal than none — it is none.
    /// </summary>
    [Fact]
    public async Task Factory_stamps_nothing_when_the_selection_names_a_tenant_the_user_has_left()
    {
        var userId = Guid.CreateVersion7();
        var left = Guid.CreateVersion7();
        var remainingA = Guid.CreateVersion7();
        var remainingB = Guid.CreateVersion7();
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(new TenantMembership(userId, left, []));
        await store.SetMembershipAsync(new TenantMembership(userId, remainingA, []));
        await store.SetMembershipAsync(new TenantMembership(userId, remainingB, []));
        await store.SetActiveTenantAsync(userId, left);
        await store.SetMembershipAsync(
            new TenantMembership(userId, left, [], MembershipStatus.Pending));

        var factory = new MembershipClaimsPrincipalFactory<IdentityUser>(
            new PassThroughFactory(_ => AuthenticatedPrincipal(userId)), store);

        var principal = await factory.CreateAsync(new IdentityUser { Id = userId.ToString() });

        Assert.Equal(left, await store.GetActiveTenantAsync(userId));
        Assert.Null(TenantClaim(principal));
    }

    /// <summary>
    /// One active membership is unambiguous and keeps resolving. This is the regression guard for
    /// every ordinary user — the case that would break if the change were drawn too wide.
    /// </summary>
    [Fact]
    public async Task Factory_stamps_the_only_active_membership_even_beside_inactive_ones()
    {
        var userId = Guid.CreateVersion7();
        var active = Guid.CreateVersion7();
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(new TenantMembership(userId, active, []));
        await store.SetMembershipAsync(
            new TenantMembership(userId, Guid.CreateVersion7(), [], MembershipStatus.Pending));
        await store.SetMembershipAsync(
            new TenantMembership(userId, Guid.CreateVersion7(), [], MembershipStatus.Pending));

        var factory = new MembershipClaimsPrincipalFactory<IdentityUser>(
            new PassThroughFactory(_ => AuthenticatedPrincipal(userId)), store);

        var principal = await factory.CreateAsync(new IdentityUser { Id = userId.ToString() });

        Assert.Equal(active.ToString("D"), TenantClaim(principal));
    }

    /// <summary>
    /// Both entry points share the resolver today. This is what keeps that true: a fix applied to one
    /// and not the other is the failure mode a shared helper is supposed to make impossible.
    /// </summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(3, true)]
    [InlineData(3, false)]
    public async Task BothEntryPoints_ResolveIdentically(int membershipCount, bool withSelection)
    {
        var userId = Guid.CreateVersion7();
        var store = new InMemoryTenantMembershipStore();
        var tenants = Enumerable.Range(0, membershipCount).Select(_ => Guid.CreateVersion7()).ToList();
        foreach (var tenant in tenants)
        {
            await store.SetMembershipAsync(new TenantMembership(userId, tenant, []));
        }

        if (withSelection)
        {
            await store.SetActiveTenantAsync(userId, tenants[^1]);
        }

        var factory = new MembershipClaimsPrincipalFactory<IdentityUser>(
            new PassThroughFactory(_ => AuthenticatedPrincipal(userId)), store);
        var fromFactory = await factory.CreateAsync(new IdentityUser { Id = userId.ToString() });

        var transformation = new MembershipClaimsTransformation(store);
        var fromTransformation = await transformation.TransformAsync(AuthenticatedPrincipal(userId));

        var expected = withSelection || membershipCount == 1 ? tenants[^1].ToString("D") : null;
        Assert.Equal(expected, TenantClaim(fromFactory));
        Assert.Equal(TenantClaim(fromFactory), TenantClaim(fromTransformation));
    }

    /// <summary>
    /// The whole change rests on the missing claim being refused downstream rather than ignored.
    /// A principal without the claim resolves to the reserved default tenant — which is none of the
    /// user's — and <c>TenantIsolationBehaviorTests.MismatchedTenant_Default_Rejected_AndHandlerNotInvoked</c>
    /// is where a session tenant that does not match the request's is shown to be rejected.
    /// </summary>
    [Fact]
    public async Task WithoutATenantClaim_TheSessionCarriesTheReservedDefaultTenant()
    {
        var userId = Guid.CreateVersion7();
        var tenants = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        var store = new InMemoryTenantMembershipStore();
        foreach (var tenant in tenants)
        {
            await store.SetMembershipAsync(new TenantMembership(userId, tenant, []));
        }

        var transformation = new MembershipClaimsTransformation(store);
        var principal = await transformation.TransformAsync(AuthenticatedPrincipal(userId));
        Assert.Null(TenantClaim(principal));

        var provider = new RecordingSessionContextProvider();
        var httpContext = new DefaultHttpContext { User = principal };
        var middleware = new SessionContextMiddleware(
            _ => Task.CompletedTask, Options.Create(new SessionContextOptions()));

        await middleware.InvokeAsync(httpContext, provider);

        var session = Assert.IsType<SessionContext>(provider.Current);
        Assert.Equal(DefaultTenantIdentifier.Value, session.TenantId);
        Assert.DoesNotContain(session.TenantId, tenants);
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
