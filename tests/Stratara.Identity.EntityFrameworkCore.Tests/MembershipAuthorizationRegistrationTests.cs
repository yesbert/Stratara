using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Authorization;
using Xunit;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

/// <summary>
/// The options are one instance every registration configures, so a host that names roles from more
/// than one place — or that calls the parameterless registration as well — keeps all of them.
/// </summary>
public class MembershipAuthorizationRegistrationTests
{
    [Fact]
    public void Two_configuring_calls_keep_both_roles()
    {
        var services = new ServiceCollection();

        services.AddMembershipAuthorization(o => o.HomeTenantRoles.Add("Service"));
        services.AddMembershipAuthorizationOptions(o => o.HomeTenantRoles.Add("Operator"));

        var options = Resolve(services);
        Assert.Contains("Service", options.HomeTenantRoles);
        Assert.Contains("Operator", options.HomeTenantRoles);
    }

    [Fact]
    public void The_parameterless_registration_does_not_drop_a_configured_role()
    {
        var services = new ServiceCollection();

        services.AddMembershipAuthorizationOptions(o => o.HomeTenantRoles.Add("Service"));
        services.AddMembershipAuthorization();

        Assert.Contains("Service", Resolve(services).HomeTenantRoles);
    }

    [Fact]
    public void The_configuring_registration_adds_to_what_came_before_it()
    {
        var services = new ServiceCollection();

        services.AddMembershipAuthorization();
        services.AddMembershipAuthorizationOptions(o => o.HomeTenantRoles.Add("Service"));

        Assert.Contains("Service", Resolve(services).HomeTenantRoles);
    }

    [Fact]
    public void Without_a_named_role_the_options_are_empty()
    {
        var services = new ServiceCollection();

        services.AddMembershipAuthorization();

        Assert.Empty(Resolve(services).HomeTenantRoles);
    }

    [Fact]
    public void The_registration_resolves_the_membership_provider()
    {
        var services = new ServiceCollection();

        services.AddMembershipAuthorization(o => o.HomeTenantRoles.Add("Service"));

        var descriptor = Assert.Single(services, s => s.ServiceType == typeof(IAuthorizationProvider));
        Assert.Equal(typeof(MembershipAuthorizationProvider), descriptor.ImplementationType);
    }

    private static MembershipAuthorizationOptions Resolve(IServiceCollection services) =>
        Assert.Single(services, s => s.ServiceType == typeof(MembershipAuthorizationOptions))
            .ImplementationInstance as MembershipAuthorizationOptions
        ?? throw new InvalidOperationException("The options were not registered as an instance.");
}
