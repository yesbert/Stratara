using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Settings;
using Xunit;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

/// <summary>
/// Scenario <em>A catalog part is declared after the host was built</em>: the catalogs and the membership options are
/// one instance every registration adds to, and a built host reads that instance. Once the host was built from the
/// service collection — which makes it read-only — a further registration fails, naming itself, and leaves the
/// instance as the host read it. Made while the host is being configured, the same registrations still add up.
/// </summary>
public class RegistrationAfterBuildTests
{
    [Fact]
    public void A_permission_catalog_part_declared_after_the_host_was_built_is_refused()
    {
        var services = new ServiceCollection();
        services.AddPermissionCatalog(c => c.Add("sims.read"));
        services.MakeReadOnly();

        var refused = Assert.Throws<InvalidOperationException>(() => services.AddPermissionCatalog(c => c.Add("sims.write")));

        Assert.StartsWith($"{nameof(IdentityDirectoryServiceCollectionExtensions.AddPermissionCatalog)} was called", refused.Message, StringComparison.Ordinal);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(["sims.read"], provider.GetRequiredService<PermissionCatalog>().All);
    }

    [Fact]
    public void A_setting_catalog_part_declared_after_the_host_was_built_is_refused()
    {
        var services = new ServiceCollection();
        services.AddSettingCatalog(c => c.Add(new SettingDefinition("ui.theme", DefaultValue: "light")));
        services.MakeReadOnly();

        var refused = Assert.Throws<InvalidOperationException>(() =>
            services.AddSettingCatalog(c => c.Add(new SettingDefinition("ui.language", DefaultValue: "en"))));

        Assert.StartsWith($"{nameof(IdentityDirectoryServiceCollectionExtensions.AddSettingCatalog)} was called", refused.Message, StringComparison.Ordinal);
        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<SettingCatalog>();
        Assert.True(catalog.Contains("ui.theme"));
        Assert.False(catalog.Contains("ui.language"));
    }

    [Fact]
    public void A_role_named_after_the_host_was_built_is_refused()
    {
        var services = new ServiceCollection();
        services.AddMembershipAuthorization(o => o.HomeTenantRoles.Add("Service"));
        services.MakeReadOnly();

        var refused = Assert.Throws<InvalidOperationException>(() =>
            services.AddMembershipAuthorization(o => o.HomeTenantRoles.Add("Operator")));
        var refusedOptions = Assert.Throws<InvalidOperationException>(() =>
            services.AddMembershipAuthorizationOptions(o => o.HomeTenantRoles.Add("Operator")));

        Assert.StartsWith($"{nameof(IdentityDirectoryServiceCollectionExtensions.AddMembershipAuthorization)} was called", refused.Message, StringComparison.Ordinal);
        Assert.StartsWith($"{nameof(IdentityDirectoryServiceCollectionExtensions.AddMembershipAuthorizationOptions)} was called", refusedOptions.Message, StringComparison.Ordinal);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(["Service"], provider.GetRequiredService<MembershipAuthorizationOptions>().HomeTenantRoles);
    }

    [Fact]
    public void A_registration_that_would_change_nothing_is_not_refused_after_the_host_was_built()
    {
        var services = new ServiceCollection();
        services.AddMembershipAuthorization(o => o.HomeTenantRoles.Add("Service"));
        services.MakeReadOnly();

        services.AddMembershipAuthorization();
        services.AddMembershipAuthorizationOptions(configure: null);

        using var provider = services.BuildServiceProvider();
        Assert.Equal(["Service"], provider.GetRequiredService<MembershipAuthorizationOptions>().HomeTenantRoles);
    }

    [Fact]
    public void Registrations_made_while_the_host_is_being_configured_still_add_up()
    {
        var services = new ServiceCollection();
        services.AddPermissionCatalog(c => c.Add("sims.read"));
        services.AddPermissionCatalog(c => c.Add("sims.write"));
        services.AddMembershipAuthorization(o => o.HomeTenantRoles.Add("Service"));
        services.AddMembershipAuthorizationOptions(o => o.HomeTenantRoles.Add("Operator"));

        using var provider = services.BuildServiceProvider();

        Assert.Equal(["sims.read", "sims.write"], provider.GetRequiredService<PermissionCatalog>().All.Order());
        Assert.Equal(["Operator", "Service"], provider.GetRequiredService<MembershipAuthorizationOptions>().HomeTenantRoles.Order());
    }
}
