using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Multitenancy;
using Stratara.Testing;
using Xunit;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

public class MembershipCrossTenantAuthorizerTests
{
    private static readonly Guid ActorTenant = Guid.CreateVersion7();
    private static readonly Guid SubjectTenant = Guid.CreateVersion7();
    private static readonly Guid ActorUser = Guid.CreateVersion7();

    private sealed class FixedRoleProvider(params string[] roles) : IAuthorizationProvider
    {
        public Task<bool> IsInRoleAsync(string role, CancellationToken cancellationToken = default) =>
            Task.FromResult(roles.Contains(role, StringComparer.Ordinal));
    }

    private static Stratara.Contracts.Session.SessionContext CrossTenantSession() =>
        TestSessionContext.ForActorAndSubject(ActorTenant, ActorUser, SubjectTenant);

    [Fact]
    public async Task Active_membership_in_the_subject_tenant_allows()
    {
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(new TenantMembership(ActorUser, SubjectTenant, []));

        var authorizer = new MembershipCrossTenantAuthorizer(
            store, new FixedRoleProvider(), new MembershipCrossTenantAuthorizerOptions());

        Assert.True(await authorizer.IsCrossTenantAllowedAsync(CrossTenantSession()));
    }

    [Fact]
    public async Task Pending_membership_does_not_allow()
    {
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(
            new TenantMembership(ActorUser, SubjectTenant, [], MembershipStatus.Pending));

        var authorizer = new MembershipCrossTenantAuthorizer(
            store, new FixedRoleProvider(), new MembershipCrossTenantAuthorizerOptions());

        Assert.False(await authorizer.IsCrossTenantAllowedAsync(CrossTenantSession()));
    }

    [Fact]
    public async Task Configured_cross_tenant_role_allows_without_membership()
    {
        var options = new MembershipCrossTenantAuthorizerOptions();
        options.CrossTenantRoles.Add("PlatformAdmin");

        var authorizer = new MembershipCrossTenantAuthorizer(
            new InMemoryTenantMembershipStore(), new FixedRoleProvider("PlatformAdmin"), options);

        Assert.True(await authorizer.IsCrossTenantAllowedAsync(CrossTenantSession()));
    }

    [Fact]
    public async Task Without_membership_and_without_matching_role_denies()
    {
        var options = new MembershipCrossTenantAuthorizerOptions();
        options.CrossTenantRoles.Add("PlatformAdmin");

        var authorizer = new MembershipCrossTenantAuthorizer(
            new InMemoryTenantMembershipStore(), new FixedRoleProvider("SomeOtherRole"), options);

        Assert.False(await authorizer.IsCrossTenantAllowedAsync(CrossTenantSession()));
    }

    [Fact]
    public async Task Configured_role_held_through_the_actors_own_membership_allows()
    {
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(new TenantMembership(ActorUser, ActorTenant, ["PlatformAdmin"]));
        var options = new MembershipCrossTenantAuthorizerOptions();
        options.CrossTenantRoles.Add("PlatformAdmin");

        // A machine actor has no global role level at all: a key becomes a membership.
        var authorizer = new MembershipCrossTenantAuthorizer(store, new FixedRoleProvider(), options);

        Assert.True(await authorizer.IsCrossTenantAllowedAsync(CrossTenantSession()));
    }

    [Fact]
    public async Task An_unconfigured_role_held_at_home_does_not_allow()
    {
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(new TenantMembership(ActorUser, ActorTenant, ["TenantAdmin"]));
        var options = new MembershipCrossTenantAuthorizerOptions();
        options.CrossTenantRoles.Add("PlatformAdmin");

        var authorizer = new MembershipCrossTenantAuthorizer(store, new FixedRoleProvider(), options);

        Assert.False(await authorizer.IsCrossTenantAllowedAsync(CrossTenantSession()));
    }

    [Fact]
    public async Task A_pending_membership_at_home_does_not_carry_the_configured_role()
    {
        var store = new InMemoryTenantMembershipStore();
        await store.SetMembershipAsync(
            new TenantMembership(ActorUser, ActorTenant, ["PlatformAdmin"], MembershipStatus.Pending));
        var options = new MembershipCrossTenantAuthorizerOptions();
        options.CrossTenantRoles.Add("PlatformAdmin");

        var authorizer = new MembershipCrossTenantAuthorizer(store, new FixedRoleProvider(), options);

        Assert.False(await authorizer.IsCrossTenantAllowedAsync(CrossTenantSession()));
    }

    [Fact]
    public async Task Default_options_deny_actors_without_membership()
    {
        var authorizer = new MembershipCrossTenantAuthorizer(
            new InMemoryTenantMembershipStore(),
            new FixedRoleProvider("PlatformAdmin"),
            new MembershipCrossTenantAuthorizerOptions());

        Assert.False(await authorizer.IsCrossTenantAllowedAsync(CrossTenantSession()));
    }
}
