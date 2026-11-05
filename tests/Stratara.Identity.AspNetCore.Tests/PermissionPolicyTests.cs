using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.Authorization;
using Stratara.Identity.AspNetCore.Authorization;
using Stratara.Sessions.Multitenancy;
using Xunit;

namespace Stratara.Identity.AspNetCore.Tests;

public class PermissionPolicyTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();

    private static PermissionCatalog Catalog()
    {
        var catalog = new PermissionCatalog();
        catalog.Add("sims.read");
        return catalog;
    }

    private static PermissionPolicyProvider Provider() =>
        new(Options.Create(new AuthorizationOptions()), Catalog());

    private static ClaimsPrincipal Principal(bool withTenantClaim = true)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, UserId.ToString()) };
        if (withTenantClaim)
        {
            claims.Add(new Claim(StrataraClaimTypes.TenantId, TenantId.ToString("D")));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private static Mock<IPermissionResolver> Resolver(params string[] held)
    {
        var mock = new Mock<IPermissionResolver>();
        mock.Setup(r => r.ResolvePermissionsAsync(UserId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(held, StringComparer.Ordinal));
        return mock;
    }

    [Fact]
    public async Task Catalog_permission_names_become_policies()
    {
        var policy = await Provider().GetPolicyAsync("sims.read");

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, r => r is PermissionRequirement { Permission: "sims.read" });
    }

    [Fact]
    public async Task Undeclared_policy_names_defer_to_the_default_provider()
    {
        Assert.Null(await Provider().GetPolicyAsync("SomeConventionalPolicy"));
        Assert.NotNull(await Provider().GetDefaultPolicyAsync());
    }

    [Fact]
    public async Task Handler_succeeds_when_the_resolver_holds_the_permission()
    {
        var requirement = new PermissionRequirement("sims.read");
        var context = new AuthorizationHandlerContext([requirement], Principal(), resource: null);

        await new PermissionAuthorizationHandler(Resolver("sims.read").Object).HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Handler_fails_closed_without_the_permission()
    {
        var requirement = new PermissionRequirement("sims.read");
        var context = new AuthorizationHandlerContext([requirement], Principal(), resource: null);

        await new PermissionAuthorizationHandler(Resolver().Object).HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Handler_fails_closed_without_a_tenant_claim()
    {
        var resolver = Resolver("sims.read");
        var requirement = new PermissionRequirement("sims.read");
        var context = new AuthorizationHandlerContext([requirement], Principal(withTenantClaim: false), resource: null);

        await new PermissionAuthorizationHandler(resolver.Object).HandleAsync(context);

        Assert.False(context.HasSucceeded);
        resolver.Verify(
            r => r.ResolvePermissionsAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Permission_policies_resolve_whether_authorization_was_added_before_or_after(bool authorizationFirst)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Catalog());
        if (authorizationFirst)
        {
            services.AddAuthorizationBuilder().AddPolicy("Conventional", p => p.RequireRole("admin"));
        }

        services.AddStrataraPermissionPolicies();
        if (!authorizationFirst)
        {
            services.AddAuthorizationBuilder().AddPolicy("Conventional", p => p.RequireRole("admin"));
        }

        await using var provider = services.BuildServiceProvider();
        var policies = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        Assert.IsType<PermissionPolicyProvider>(policies);
        var permission = await policies.GetPolicyAsync("sims.read");
        Assert.NotNull(permission);
        Assert.Contains(permission.Requirements, r => r is PermissionRequirement { Permission: "sims.read" });
        Assert.NotNull(await policies.GetPolicyAsync("Conventional"));
        Assert.Null(await policies.GetPolicyAsync("Undeclared"));
        Assert.NotNull(await policies.GetDefaultPolicyAsync());
    }

    [Fact]
    public async Task A_fallback_policy_the_host_configures_is_still_served()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Catalog());
        services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddStrataraPermissionPolicies();

        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_policy_provider_the_host_supplies_is_kept(bool authorizationFirst)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Catalog());
        if (authorizationFirst)
        {
            services.AddAuthorization();
        }

        services.AddSingleton<IAuthorizationPolicyProvider, HostPolicyProvider>();
        services.AddStrataraPermissionPolicies();
        services.AddAuthorization();

        await using var provider = services.BuildServiceProvider();

        Assert.IsType<HostPolicyProvider>(provider.GetRequiredService<IAuthorizationPolicyProvider>());
    }

    private sealed class HostPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options);
}
