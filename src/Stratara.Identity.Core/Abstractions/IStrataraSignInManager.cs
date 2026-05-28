using Stratara.Identity.Core.Models;

namespace Stratara.Identity.Core.Abstractions;

/// <summary>
/// Channel-agnostic sign-in manager surface. Implementations wrap the host's native identity machinery
/// (e.g. ASP.NET Core <c>SignInManager&lt;TUser&gt;</c> for server-side Blazor; HTTP calls to the identity
/// endpoint for non-web hosts) and produce the unified <see cref="StrataraSignInResult"/>.
/// </summary>
public interface IStrataraSignInManager
{
    /// <summary>Performs a password sign-in.</summary>
    /// <param name="email">User email.</param>
    /// <param name="password">User password.</param>
    /// <param name="isPersistent">Whether the resulting session should persist.</param>
    /// <param name="lockoutOnFailure">Whether repeated failures should advance the lockout counter.</param>
    /// <param name="cancellationToken">Propagated to the underlying identity stack and any HTTP / DB calls.</param>
    Task<StrataraSignInResult> PasswordSignInAsync(string email, string password, bool isPersistent, bool lockoutOnFailure, CancellationToken cancellationToken = default);

    /// <summary>Completes a two-factor sign-in step using a TOTP code.</summary>
    /// <param name="code">TOTP authenticator code.</param>
    /// <param name="isPersistent">Whether the resulting session should persist.</param>
    /// <param name="rememberClient">Whether to remember this client to skip future two-factor prompts.</param>
    /// <param name="cancellationToken">Propagated to the underlying identity stack and any HTTP / DB calls.</param>
    Task<StrataraSignInResult> TwoFactorAuthenticatorSignInAsync(string code, bool isPersistent, bool rememberClient, CancellationToken cancellationToken = default);

    /// <summary>Completes a two-factor sign-in step using a recovery code.</summary>
    /// <param name="recoveryCode">Recovery code from the user's printed list.</param>
    /// <param name="cancellationToken">Propagated to the underlying identity stack and any HTTP / DB calls.</param>
    Task<StrataraSignInResult> TwoFactorRecoveryCodeSignInAsync(string recoveryCode, CancellationToken cancellationToken = default);

    /// <summary>Refreshes the current sign-in using the given <see cref="AccessTokenInfo"/>'s refresh token.</summary>
    /// <param name="tokenInfo">The current access-token info.</param>
    /// <param name="cancellationToken">Propagated to the refresh HTTP call and any subsequent token-persist.</param>
    /// <returns><c>true</c> when the refresh succeeded and a new token was persisted; <c>false</c> otherwise.</returns>
    Task<bool> RefreshSignInAsync(AccessTokenInfo tokenInfo, CancellationToken cancellationToken = default);

    /// <summary>Signs the current user out and clears any persisted token state.</summary>
    /// <param name="cancellationToken">Propagated to the underlying identity stack and any HTTP / DB calls.</param>
    Task SignOutAsync(CancellationToken cancellationToken = default);
}
