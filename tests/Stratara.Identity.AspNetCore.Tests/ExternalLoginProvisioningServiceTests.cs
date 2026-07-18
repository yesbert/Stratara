using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stratara.Identity.AspNetCore.Authentication;
using Stratara.Identity.AspNetCore.Services;

namespace Stratara.Identity.AspNetCore.Tests;

public class ExternalLoginProvisioningServiceTests
{
    private const string Provider = "OpenIdConnect";
    private const string Sub = "sub-9f3c-stable-subject";
    private const string Email = "alice@example.com";

    private sealed class TestIdentityDbContext(DbContextOptions<TestIdentityDbContext> options)
        : IdentityDbContext<IdentityUser>(options);

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestIdentityDbContext>(o => o.UseInMemoryDatabase("prov-" + Guid.CreateVersion7()));
        services.AddIdentityCore<IdentityUser>()
            .AddEntityFrameworkStores<TestIdentityDbContext>();
        return services.BuildServiceProvider();
    }

    private static ExternalLoginProvisioningService<IdentityUser> ServiceFor(
        UserManager<IdentityUser> userManager, ExternalLoginProvisioningOptions? options = null) =>
        new(userManager, Options.Create(options ?? new ExternalLoginProvisioningOptions()), NullLogger<ExternalLoginProvisioningService<IdentityUser>>.Instance);

    private static ExternalLoginInfo LoginInfo(string? email = Email, bool? emailVerified = true, string providerKey = Sub)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, providerKey) };
        if (email is not null)
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
        }

        if (emailVerified is { } verified)
        {
            claims.Add(new Claim("email_verified", verified ? "true" : "false"));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Provider));
        return new ExternalLoginInfo(principal, Provider, providerKey, Provider);
    }

    [Fact]
    public async Task Already_linked_login_signs_in_the_existing_account()
    {
        var provider = BuildProvider();
        var userManager = provider.GetRequiredService<UserManager<IdentityUser>>();
        var user = new IdentityUser { UserName = Email, Email = Email, EmailConfirmed = true };
        await userManager.CreateAsync(user);
        await userManager.AddLoginAsync(user, new UserLoginInfo(Provider, Sub, Provider));

        var result = await ServiceFor(userManager).ProvisionAsync(LoginInfo());

        Assert.Equal(ExternalLoginProvisioningOutcome.SignedInExisting, result.Outcome);
        Assert.Equal(user.Id, result.User!.Id);
    }

    [Fact]
    public async Task First_verified_sign_in_provisions_and_links_on_the_subject_not_the_email()
    {
        var provider = BuildProvider();
        var userManager = provider.GetRequiredService<UserManager<IdentityUser>>();

        var result = await ServiceFor(userManager).ProvisionAsync(LoginInfo(email: "alice+alias@example.com"));

        Assert.Equal(ExternalLoginProvisioningOutcome.Provisioned, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.True(result.User!.EmailConfirmed);

        var byLogin = await userManager.FindByLoginAsync(Provider, Sub);
        Assert.NotNull(byLogin);
        Assert.Equal(result.User.Id, byLogin!.Id);

        var logins = await userManager.GetLoginsAsync(result.User);
        Assert.Equal(Sub, Assert.Single(logins).ProviderKey);
    }

    [Fact]
    public async Task Unverified_email_matching_an_existing_account_requires_interactive_linking()
    {
        var provider = BuildProvider();
        var userManager = provider.GetRequiredService<UserManager<IdentityUser>>();
        var existing = new IdentityUser { UserName = Email, Email = Email, EmailConfirmed = true };
        await userManager.CreateAsync(existing);

        var result = await ServiceFor(userManager).ProvisionAsync(LoginInfo(emailVerified: false));

        Assert.Equal(ExternalLoginProvisioningOutcome.RequiresInteractiveLinking, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Null(await userManager.FindByLoginAsync(Provider, Sub));
    }

    [Fact]
    public async Task Verified_email_matching_a_confirmed_account_is_auto_linked()
    {
        var provider = BuildProvider();
        var userManager = provider.GetRequiredService<UserManager<IdentityUser>>();
        var existing = new IdentityUser { UserName = Email, Email = Email, EmailConfirmed = true };
        await userManager.CreateAsync(existing);

        var result = await ServiceFor(userManager).ProvisionAsync(LoginInfo(emailVerified: true));

        Assert.Equal(ExternalLoginProvisioningOutcome.Linked, result.Outcome);
        Assert.Equal(existing.Id, result.User!.Id);
        Assert.NotNull(await userManager.FindByLoginAsync(Provider, Sub));
    }

    [Fact]
    public async Task Verified_provider_but_unconfirmed_local_account_still_blocks_auto_link()
    {
        var provider = BuildProvider();
        var userManager = provider.GetRequiredService<UserManager<IdentityUser>>();
        var existing = new IdentityUser { UserName = Email, Email = Email, EmailConfirmed = false };
        await userManager.CreateAsync(existing);

        var result = await ServiceFor(userManager).ProvisionAsync(LoginInfo(emailVerified: true));

        Assert.Equal(ExternalLoginProvisioningOutcome.RequiresInteractiveLinking, result.Outcome);
        Assert.Null(await userManager.FindByLoginAsync(Provider, Sub));
    }

    [Fact]
    public async Task AutoProvision_disabled_denies_an_unmatched_sign_in()
    {
        var provider = BuildProvider();
        var userManager = provider.GetRequiredService<UserManager<IdentityUser>>();

        var result = await ServiceFor(userManager, new ExternalLoginProvisioningOptions { AutoProvision = false })
            .ProvisionAsync(LoginInfo());

        Assert.Equal(ExternalLoginProvisioningOutcome.Denied, result.Outcome);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task Invitation_gate_rejection_denies_and_creates_nothing()
    {
        var provider = BuildProvider();
        var userManager = provider.GetRequiredService<UserManager<IdentityUser>>();
        var options = new ExternalLoginProvisioningOptions { InvitationGate = (_, _) => Task.FromResult(false) };

        var result = await ServiceFor(userManager, options).ProvisionAsync(LoginInfo());

        Assert.Equal(ExternalLoginProvisioningOutcome.Denied, result.Outcome);
        Assert.Null(await userManager.FindByLoginAsync(Provider, Sub));
    }

    [Fact]
    public async Task Invitation_gate_receives_the_resolved_email_and_verified_flag()
    {
        var provider = BuildProvider();
        var userManager = provider.GetRequiredService<UserManager<IdentityUser>>();
        ExternalLoginProvisioningContext? seen = null;
        var options = new ExternalLoginProvisioningOptions
        {
            InvitationGate = (ctx, _) =>
            {
                seen = ctx;
                return Task.FromResult(true);
            },
        };

        await ServiceFor(userManager, options).ProvisionAsync(LoginInfo(emailVerified: true));

        Assert.NotNull(seen);
        Assert.Equal(Sub, seen!.ProviderKey);
        Assert.Equal(Email, seen.Email);
        Assert.True(seen.EmailVerifiedByProvider);
    }

    [Fact]
    public async Task Provisioning_without_an_email_is_denied()
    {
        var provider = BuildProvider();
        var userManager = provider.GetRequiredService<UserManager<IdentityUser>>();

        var result = await ServiceFor(userManager).ProvisionAsync(LoginInfo(email: null, emailVerified: null));

        Assert.Equal(ExternalLoginProvisioningOutcome.Denied, result.Outcome);
    }

    [Fact]
    public async Task Xms_edov_claim_is_honored_as_a_verified_signal()
    {
        var provider = BuildProvider();
        var userManager = provider.GetRequiredService<UserManager<IdentityUser>>();
        var existing = new IdentityUser { UserName = Email, Email = Email, EmailConfirmed = true };
        await userManager.CreateAsync(existing);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Sub),
            new(ClaimTypes.Email, Email),
            new("xms_edov", "true"),
        };
        var loginInfo = new ExternalLoginInfo(
            new ClaimsPrincipal(new ClaimsIdentity(claims, Provider)), Provider, Sub, Provider);

        var result = await ServiceFor(userManager).ProvisionAsync(loginInfo);

        Assert.Equal(ExternalLoginProvisioningOutcome.Linked, result.Outcome);
    }
}
