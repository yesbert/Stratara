using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Identity.AspNetCore.Authentication;

namespace Stratara.Identity.AspNetCore.Services;

/// <summary>
/// Just-in-time provisioning for external (OpenID Connect) sign-ins: on a first external sign-in it
/// creates a local ASP.NET Identity account and links the external login, or links to an existing
/// account — enforcing the security invariants as hard defaults.
/// </summary>
/// <remarks>
/// This is a channel-agnostic callable service; the sign-in callback endpoint that invokes it stays
/// in the consumer app. The invariants it enforces: link on the issuer's <c>(provider, sub)</c>,
/// never on email; auto-link to a pre-existing account only when the email is verified by the
/// provider and confirmed locally (otherwise <see cref="ExternalLoginProvisioningOutcome.RequiresInteractiveLinking"/>,
/// never a silent merge); honor the invitation gate and the auto-provision switch; and fail closed
/// whenever a check cannot be satisfied.
/// </remarks>
/// <typeparam name="TUser">The host's ASP.NET Identity user entity.</typeparam>
/// <param name="userManager">The ASP.NET Identity user manager backing local accounts.</param>
/// <param name="options">The provisioning options (security invariants).</param>
/// <param name="logger">The logger for provisioning outcomes.</param>
public sealed class ExternalLoginProvisioningService<TUser>(
    UserManager<TUser> userManager,
    IOptions<ExternalLoginProvisioningOptions> options,
    ILogger<ExternalLoginProvisioningService<TUser>> logger)
    where TUser : IdentityUser, new()
{
    private readonly ExternalLoginProvisioningOptions _options = options.Value;

    /// <summary>
    /// Resolves the local account for an external sign-in, provisioning or linking as policy allows.
    /// </summary>
    /// <param name="loginInfo">The external login info from <c>SignInManager.GetExternalLoginInfoAsync()</c>.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>The provisioning outcome and, when available, the resolved local account.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loginInfo"/> is <c>null</c>.</exception>
    public async Task<ExternalLoginProvisioningResult<TUser>> ProvisionAsync(
        ExternalLoginInfo loginInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loginInfo);
        cancellationToken.ThrowIfCancellationRequested();

        var alreadyLinked = await userManager.FindByLoginAsync(loginInfo.LoginProvider, loginInfo.ProviderKey);
        if (alreadyLinked is not null)
        {
            return new ExternalLoginProvisioningResult<TUser>(
                ExternalLoginProvisioningOutcome.SignedInExisting, alreadyLinked, null);
        }

        var email = ReadEmail(loginInfo.Principal);
        var emailVerifiedByProvider = ReadEmailVerified(loginInfo.Principal, _options.EmailVerifiedClaimTypes);
        var context = new ExternalLoginProvisioningContext(
            loginInfo.LoginProvider, loginInfo.ProviderKey, email, emailVerifiedByProvider, loginInfo.Principal);

        if (_options.InvitationGate is { } gate && !await gate(context, cancellationToken))
        {
            return Denied(loginInfo.LoginProvider, "invitation gate rejected the sign-in");
        }

        var existingByEmail = email is null ? null : await userManager.FindByEmailAsync(email);
        if (existingByEmail is not null)
        {
            return await LinkToExistingAsync(existingByEmail, loginInfo, emailVerifiedByProvider);
        }

        if (!_options.AutoProvision)
        {
            return Denied(loginInfo.LoginProvider, "auto-provisioning is disabled and no matching account exists");
        }

        if (email is null)
        {
            return Denied(loginInfo.LoginProvider, "the external login carried no email to provision an account from");
        }

        return await ProvisionNewAsync(loginInfo, email, emailVerifiedByProvider);
    }

    private async Task<ExternalLoginProvisioningResult<TUser>> LinkToExistingAsync(
        TUser existing, ExternalLoginInfo loginInfo, bool emailVerifiedByProvider)
    {
        var canAutoLink = !_options.RequireVerifiedEmailForLinking
                          || (emailVerifiedByProvider && existing.EmailConfirmed);
        if (!canAutoLink)
        {
            ExternalLoginProvisioningLog.LinkRefusedUnverified(logger, loginInfo.LoginProvider);
            return new ExternalLoginProvisioningResult<TUser>(
                ExternalLoginProvisioningOutcome.RequiresInteractiveLinking,
                existing,
                "email not verified on both sides — interactive linking required");
        }

        var linked = await userManager.AddLoginAsync(existing, ToUserLoginInfo(loginInfo));
        if (!linked.Succeeded)
        {
            return Denied(loginInfo.LoginProvider, DescribeErrors(linked));
        }

        ExternalLoginProvisioningLog.LoginLinked(logger, loginInfo.LoginProvider);
        return new ExternalLoginProvisioningResult<TUser>(
            ExternalLoginProvisioningOutcome.Linked, existing, null);
    }

    private async Task<ExternalLoginProvisioningResult<TUser>> ProvisionNewAsync(
        ExternalLoginInfo loginInfo, string email, bool emailVerifiedByProvider)
    {
        var user = new TUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = emailVerifiedByProvider,
        };

        var created = await userManager.CreateAsync(user);
        if (!created.Succeeded)
        {
            return Denied(loginInfo.LoginProvider, DescribeErrors(created));
        }

        var linked = await userManager.AddLoginAsync(user, ToUserLoginInfo(loginInfo));
        if (!linked.Succeeded)
        {
            return Denied(loginInfo.LoginProvider, DescribeErrors(linked));
        }

        ExternalLoginProvisioningLog.UserProvisioned(logger, loginInfo.LoginProvider);
        return new ExternalLoginProvisioningResult<TUser>(
            ExternalLoginProvisioningOutcome.Provisioned, user, null);
    }

    private ExternalLoginProvisioningResult<TUser> Denied(string loginProvider, string reason)
    {
        ExternalLoginProvisioningLog.ProvisioningDenied(logger, loginProvider, reason);
        return new ExternalLoginProvisioningResult<TUser>(
            ExternalLoginProvisioningOutcome.Denied, null, reason);
    }

    private static UserLoginInfo ToUserLoginInfo(ExternalLoginInfo loginInfo) =>
        new(loginInfo.LoginProvider, loginInfo.ProviderKey, loginInfo.ProviderDisplayName ?? loginInfo.LoginProvider);

    private static string? ReadEmail(ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email");

    private static bool ReadEmailVerified(ClaimsPrincipal principal, IEnumerable<string> claimTypes) =>
        claimTypes
            .Select(principal.FindFirstValue)
            .Any(value => value is not null && IsTruthy(value));

    private static bool IsTruthy(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";

    private static string DescribeErrors(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(e => e.Description));
}
