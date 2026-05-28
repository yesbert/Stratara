using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;
using Stratara.Identity.AspNetCore.Resources;
using Stratara.Identity.Core.Abstractions;
using Stratara.Identity.Core.Models;

namespace Stratara.Identity.AspNetCore.Services;

/// <summary>
/// Channel-agnostic ASP.NET Core implementation of <see cref="IStrataraSignInManager"/> that wraps the ASP.NET Core
/// <see cref="SignInManager{TUser}"/> + <see cref="UserManager{TUser}"/> and translates every outcome into the unified
/// <see cref="StrataraSignInResult"/>. Failure messages are resolved via
/// <see cref="IStringLocalizer{T}"/> (anchored on <see cref="IdentityResources"/>), so the current
/// <see cref="System.Globalization.CultureInfo.CurrentUICulture"/> decides the language. English is the default
/// resource set; German overrides ship as <c>IdentityResources.de.resx</c>.
/// </summary>
/// <typeparam name="TUser">The ASP.NET Core Identity user type.</typeparam>
public sealed class AspNetSignInManager<TUser>(
    SignInManager<TUser> signInManager,
    UserManager<TUser> userManager,
    IStringLocalizer<IdentityResources> localizer)
    : IStrataraSignInManager where TUser : class, new()
{
    private const string LockoutKey = "Identity.SignIn.Lockout";
    private const string InvalidCredentialsKey = "Identity.SignIn.InvalidCredentials";
    private const string InvalidTwoFactorKey = "Identity.SignIn.InvalidTwoFactor";
    private const string InvalidRecoveryCodeKey = "Identity.SignIn.InvalidRecoveryCode";

    /// <inheritdoc/>
    public async Task<StrataraSignInResult> PasswordSignInAsync(string email, string password, bool isPersistent,
        bool lockoutOnFailure, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var signInResult = await signInManager.PasswordSignInAsync(email, password, isPersistent, lockoutOnFailure);

        if (signInResult.IsLockedOut)
        {
            return new StrataraSignInResult(false, localizer[LockoutKey], null, isLockedOut: true);
        }

        if (signInResult.RequiresTwoFactor)
        {
            var user = await userManager.FindByEmailAsync(email);
            var userId = user is not null ? Guid.Parse(await userManager.GetUserIdAsync(user)) : (Guid?)null;
            return new StrataraSignInResult(false, null, null, userId, requiresTwoFactor: true);
        }

        if (signInResult.IsNotAllowed || !signInResult.Succeeded)
        {
            return new StrataraSignInResult(false, localizer[InvalidCredentialsKey], null, isNotAllowed: signInResult.IsNotAllowed);
        }

        var successUser = await userManager.FindByEmailAsync(email);
        var successUserId = successUser is not null ? Guid.Parse(await userManager.GetUserIdAsync(successUser)) : (Guid?)null;

        return new StrataraSignInResult(true, null, null, successUserId);
    }

    /// <inheritdoc/>
    public async Task<StrataraSignInResult> TwoFactorAuthenticatorSignInAsync(string code, bool isPersistent, bool rememberClient, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var signInResult = await signInManager.TwoFactorAuthenticatorSignInAsync(code, isPersistent, rememberClient);

        if (signInResult.IsLockedOut)
        {
            return new StrataraSignInResult(false, localizer[LockoutKey], null, isLockedOut: true);
        }

        if (!signInResult.Succeeded)
        {
            return new StrataraSignInResult(false, localizer[InvalidTwoFactorKey], null);
        }

        var user = await signInManager.GetTwoFactorAuthenticationUserAsync();
        var userId = user is not null ? Guid.Parse(await userManager.GetUserIdAsync(user)) : (Guid?)null;

        return new StrataraSignInResult(true, null, null, userId);
    }

    /// <inheritdoc/>
    public async Task<StrataraSignInResult> TwoFactorRecoveryCodeSignInAsync(string recoveryCode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var signInResult = await signInManager.TwoFactorRecoveryCodeSignInAsync(recoveryCode);

        if (signInResult.IsLockedOut)
        {
            return new StrataraSignInResult(false, localizer[LockoutKey], null, isLockedOut: true);
        }

        if (!signInResult.Succeeded)
        {
            return new StrataraSignInResult(false, localizer[InvalidRecoveryCodeKey], null);
        }

        var user = await signInManager.GetTwoFactorAuthenticationUserAsync();
        var userId = user is not null ? Guid.Parse(await userManager.GetUserIdAsync(user)) : (Guid?)null;

        return new StrataraSignInResult(true, null, null, userId);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Not supported on server-side Blazor. The authentication session is managed by the ASP.NET Core
    /// authentication middleware (cookie-based), so there is no token-refresh flow. Token-refresh applies
    /// to non-web hosts (mobile, desktop) that talk to the identity HTTP endpoints directly.
    /// </remarks>
    public Task<bool> RefreshSignInAsync(AccessTokenInfo tokenInfo, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "RefreshSignInAsync is not supported on server-side Blazor. The authentication session is " +
            "managed by ASP.NET Core's cookie middleware. Token refresh only applies to token-based flows " +
            "(mobile, desktop, native).");

    /// <inheritdoc/>
    public Task SignOutAsync(CancellationToken cancellationToken = default) => signInManager.SignOutAsync();
}
