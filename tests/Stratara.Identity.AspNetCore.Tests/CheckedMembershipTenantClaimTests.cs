using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Multitenancy;
using Stratara.Identity.AspNetCore.Services;
using Stratara.Sessions.Multitenancy;
using Stratara.Testing;
using Xunit;

namespace Stratara.Identity.AspNetCore.Tests;

/// <summary>
/// <c>tenant-directory</c> → <em>The tenant claim can be stamped at sign-in or resolved per request</em>, the
/// kept-and-checked mode: the tenant stamped at sign-in survives a selection moved elsewhere and a cookie refresh, a
/// re-issue switches it, and an ended membership drops it on the next request without a substitute. The refresh is
/// driven through the <see cref="SecurityStampValidatorOptions.OnRefreshingPrincipal"/> the registration installs,
/// which is what the security-stamp validator calls for a cookie sign-in.
/// </summary>
public class CheckedMembershipTenantClaimTests
{
    private sealed class PassThroughFactory(Func<IdentityUser, ClaimsPrincipal> create)
        : IUserClaimsPrincipalFactory<IdentityUser>
    {
        public Task<ClaimsPrincipal> CreateAsync(IdentityUser user) => Task.FromResult(create(user));
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _requestScope;

        public Harness(InMemoryTenantMembershipStore store, Guid userId)
        {
            var services = new ServiceCollection();
            services.AddSingleton<ITenantMembershipStore>(store);
            services.AddScoped<IUserClaimsPrincipalFactory<IdentityUser>>(_ =>
                new PassThroughFactory(_ => AuthenticatedPrincipal(userId)));
            services.AddCheckedMembershipTenantClaim<IdentityUser>();
            _provider = services.BuildServiceProvider();
            _requestScope = _provider.CreateScope();
            _provider.GetRequiredService<IHttpContextAccessor>().HttpContext =
                new DefaultHttpContext { RequestServices = _requestScope.ServiceProvider };
        }

        public IUserClaimsPrincipalFactory<IdentityUser> Factory =>
            _requestScope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<IdentityUser>>();

        public IClaimsTransformation Transformation =>
            _requestScope.ServiceProvider.GetRequiredService<IClaimsTransformation>();

        public async Task<ClaimsPrincipal> RefreshAsync(ClaimsPrincipal current, IdentityUser user)
        {
            var context = new SecurityStampRefreshingPrincipalContext
            {
                CurrentPrincipal = current,
                NewPrincipal = await Factory.CreateAsync(user),
            };
            await _provider.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value.OnRefreshingPrincipal!(context);
            return context.NewPrincipal!;
        }

        public void Dispose()
        {
            _requestScope.Dispose();
            _provider.Dispose();
        }
    }

    private static ClaimsPrincipal AuthenticatedPrincipal(Guid userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], IdentityConstants.ApplicationScheme));

    private static string? TenantClaim(ClaimsPrincipal principal) =>
        principal.FindFirst(StrataraClaimTypes.TenantId)?.Value;

    private static async Task<(InMemoryTenantMembershipStore Store, Guid User, Guid TenantA, Guid TenantB)> TwoTenantsAsync()
    {
        var store = new InMemoryTenantMembershipStore();
        var user = Guid.CreateVersion7();
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();
        await store.SetMembershipAsync(new TenantMembership(user, tenantA, []));
        await store.SetMembershipAsync(new TenantMembership(user, tenantB, []));
        await store.SetActiveTenantAsync(user, tenantA);
        return (store, user, tenantA, tenantB);
    }

    [Fact]
    public async Task A_selection_moved_elsewhere_moves_neither_the_request_nor_the_cookie_refresh()
    {
        var (store, user, tenantA, tenantB) = await TwoTenantsAsync();
        using var harness = new Harness(store, user);
        var identityUser = new IdentityUser { Id = user.ToString() };
        var signedIn = await harness.Factory.CreateAsync(identityUser);
        Assert.Equal(tenantA.ToString("D"), TenantClaim(signedIn));

        await store.SetActiveTenantAsync(user, tenantB);

        Assert.Equal(tenantA.ToString("D"), TenantClaim(await harness.Transformation.TransformAsync(signedIn)));
        var refreshed = await harness.RefreshAsync(signedIn, identityUser);
        Assert.Equal(tenantA.ToString("D"), TenantClaim(refreshed));
        Assert.Single(refreshed.FindAll(StrataraClaimTypes.TenantId));
    }

    [Fact]
    public async Task A_re_issue_after_the_host_selects_another_tenant_switches_it()
    {
        var (store, user, _, tenantB) = await TwoTenantsAsync();
        using var harness = new Harness(store, user);

        await store.SetActiveTenantAsync(user, tenantB);
        var reissued = await harness.Factory.CreateAsync(new IdentityUser { Id = user.ToString() });

        Assert.Equal(tenantB.ToString("D"), TenantClaim(await harness.Transformation.TransformAsync(reissued)));
    }

    [Theory]
    [InlineData(MembershipStatus.Former)]
    [InlineData(MembershipStatus.Pending)]
    public async Task An_ended_membership_drops_the_claim_on_the_next_request_without_a_substitute(MembershipStatus status)
    {
        var (store, user, tenantA, tenantB) = await TwoTenantsAsync();
        using var harness = new Harness(store, user);
        var signedIn = await harness.Factory.CreateAsync(new IdentityUser { Id = user.ToString() });
        await store.SetActiveTenantAsync(user, tenantB);

        await store.SetMembershipAsync(new TenantMembership(user, tenantA, [], status));

        var request = await harness.Transformation.TransformAsync(signedIn);
        Assert.Null(TenantClaim(request));
        Assert.Equal(tenantA.ToString("D"), TenantClaim(signedIn));
    }

    [Fact]
    public async Task A_removed_membership_drops_the_claim_on_the_next_request()
    {
        var (store, user, tenantA, _) = await TwoTenantsAsync();
        using var harness = new Harness(store, user);
        var signedIn = await harness.Factory.CreateAsync(new IdentityUser { Id = user.ToString() });

        await store.RemoveMembershipAsync(user, tenantA);

        Assert.Null(TenantClaim(await harness.Transformation.TransformAsync(signedIn)));
    }

    [Fact]
    public async Task A_refresh_after_the_membership_ended_carries_no_tenant_and_no_substitute()
    {
        var (store, user, tenantA, tenantB) = await TwoTenantsAsync();
        using var harness = new Harness(store, user);
        var identityUser = new IdentityUser { Id = user.ToString() };
        var signedIn = await harness.Factory.CreateAsync(identityUser);
        await store.SetActiveTenantAsync(user, tenantB);
        await store.SetMembershipAsync(new TenantMembership(user, tenantA, [], MembershipStatus.Former));

        var refreshed = await harness.RefreshAsync(signedIn, identityUser);

        Assert.Null(TenantClaim(refreshed));
    }

    [Fact]
    public async Task A_principal_without_the_claim_is_never_resolved_live()
    {
        var (store, user, _, _) = await TwoTenantsAsync();
        using var harness = new Harness(store, user);
        var withoutClaim = AuthenticatedPrincipal(user);

        Assert.Same(withoutClaim, await harness.Transformation.TransformAsync(withoutClaim));
    }

    [Fact]
    public async Task A_claim_whose_user_cannot_be_read_is_dropped()
    {
        var (store, _, tenantA, _) = await TwoTenantsAsync();
        var transformation = new CheckedMembershipClaimsTransformation(store);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "not-a-guid"), new Claim(StrataraClaimTypes.TenantId, tenantA.ToString("D"))],
            IdentityConstants.ApplicationScheme));

        Assert.Null(TenantClaim(await transformation.TransformAsync(principal)));
    }

    [Fact]
    public void Registering_both_the_checked_mode_and_live_resolution_fails_in_either_order()
    {
        static IServiceCollection WithFactory() => new ServiceCollection()
            .AddScoped<IUserClaimsPrincipalFactory<IdentityUser>>(_ => new PassThroughFactory(_ => new ClaimsPrincipal()));

        var liveFirst = WithFactory().AddMembershipTenantClaimsTransformation();
        Assert.Throws<InvalidOperationException>(() => liveFirst.AddCheckedMembershipTenantClaim<IdentityUser>());

        var checkedFirst = WithFactory().AddCheckedMembershipTenantClaim<IdentityUser>();
        Assert.Throws<InvalidOperationException>(() => checkedFirst.AddMembershipTenantClaimsTransformation());
    }

    [Fact]
    public void Registering_the_checked_mode_twice_decorates_the_factory_once()
    {
        var services = new ServiceCollection()
            .AddScoped<IUserClaimsPrincipalFactory<IdentityUser>>(_ => new PassThroughFactory(_ => new ClaimsPrincipal()));

        services.AddCheckedMembershipTenantClaim<IdentityUser>().AddCheckedMembershipTenantClaim<IdentityUser>();

        Assert.Single(services, d => d.ServiceType == typeof(IClaimsTransformation));
        Assert.Single(services, d => d.ServiceType == typeof(IUserClaimsPrincipalFactory<IdentityUser>));
    }

    private sealed class CountingStore(ITenantMembershipStore inner) : ITenantMembershipStore
    {
        public int Lookups { get; private set; }

        public Task<IReadOnlyList<TenantMembership>> GetMembershipsAsync(Guid userId, CancellationToken cancellationToken = default) =>
            inner.GetMembershipsAsync(userId, cancellationToken);

        public Task<TenantMembership?> GetMembershipAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken = default)
        {
            Lookups++;
            return inner.GetMembershipAsync(userId, tenantId, cancellationToken);
        }

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

    private sealed class HostTransformation : IClaimsTransformation
    {
        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal) => Task.FromResult(principal);
    }

    [Fact]
    public async Task A_principal_another_scheme_issued_keeps_its_tenant()
    {
        var store = new InMemoryTenantMembershipStore();
        var transformation = new CheckedMembershipClaimsTransformation(store);
        var machine = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "billing-service"), new Claim(StrataraClaimTypes.TenantId, Guid.CreateVersion7().ToString("D"))],
            "Bearer"));

        Assert.Same(machine, await transformation.TransformAsync(machine));
    }

    [Fact]
    public async Task A_request_authenticated_twice_looks_the_membership_up_once()
    {
        var (store, user, tenantA, _) = await TwoTenantsAsync();
        var counting = new CountingStore(store);
        var transformation = new CheckedMembershipClaimsTransformation(counting);
        var principal = AuthenticatedPrincipal(user);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(StrataraClaimTypes.TenantId, tenantA.ToString("D")));

        await transformation.TransformAsync(principal);
        await transformation.TransformAsync(principal);

        Assert.Equal(1, counting.Lookups);
    }

    [Fact]
    public async Task A_host_transformation_registered_afterwards_is_refused_at_start()
    {
        var services = new ServiceCollection()
            .AddSingleton<ITenantMembershipStore>(new InMemoryTenantMembershipStore())
            .AddScoped<IUserClaimsPrincipalFactory<IdentityUser>>(_ => new PassThroughFactory(_ => new ClaimsPrincipal()))
            .AddCheckedMembershipTenantClaim<IdentityUser>()
            .AddScoped<IClaimsTransformation, HostTransformation>();
        await using var provider = services.BuildServiceProvider();

        var guard = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Single();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => guard.StartAsync(CancellationToken.None));
        Assert.Contains(nameof(HostTransformation), refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_checked_mode_alone_starts()
    {
        var services = new ServiceCollection()
            .AddSingleton<ITenantMembershipStore>(new InMemoryTenantMembershipStore())
            .AddScoped<IUserClaimsPrincipalFactory<IdentityUser>>(_ => new PassThroughFactory(_ => new ClaimsPrincipal()))
            .AddCheckedMembershipTenantClaim<IdentityUser>();
        await using var provider = services.BuildServiceProvider();

        var guard = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Single();

        var exception = await Record.ExceptionAsync(() => guard.StartAsync(CancellationToken.None));

        Assert.Null(exception);
    }
}
