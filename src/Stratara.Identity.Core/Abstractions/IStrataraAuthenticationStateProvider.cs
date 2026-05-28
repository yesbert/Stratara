using Stratara.Identity.Core.Models;

namespace Stratara.Identity.Core.Abstractions;

/// <summary>
/// Channel-agnostic authentication-state surface that brokers sign-in / sign-out and exposes the current
/// access-token information. Implemented per host (e.g. <c>BlazorAuthenticationStateProvider</c> for server-side
/// Blazor; consumer-supplied for non-web hosts) and delegated to the matching <see cref="IStrataraSignInManager"/>.
/// </summary>
public interface IStrataraAuthenticationStateProvider
{
    /// <summary>Performs a password sign-in.</summary>
    /// <param name="email">User email.</param>
    /// <param name="password">User password.</param>
    /// <param name="isPersistent">Whether the resulting session should persist across browser/app restarts.</param>
    /// <param name="lockoutOnFailure">Whether repeated failures should advance the lockout counter.</param>
    /// <param name="cancellationToken">Propagated to the underlying sign-in manager.</param>
    Task<StrataraSignInResult> PasswordSignInAsync(string email, string password, bool isPersistent, bool lockoutOnFailure, CancellationToken cancellationToken = default);

    /// <summary>Completes a two-factor sign-in step using a TOTP code.</summary>
    /// <param name="code">TOTP authenticator code.</param>
    /// <param name="isPersistent">Whether the resulting session should persist.</param>
    /// <param name="rememberClient">Whether to remember this client to skip future two-factor prompts.</param>
    /// <param name="cancellationToken">Propagated to the underlying sign-in manager.</param>
    Task<StrataraSignInResult> TwoFactorAuthenticatorSignInAsync(string code, bool isPersistent, bool rememberClient, CancellationToken cancellationToken = default);

    /// <summary>Completes a two-factor sign-in step using a single-use recovery code.</summary>
    /// <param name="recoveryCode">Recovery code from the user's printed list.</param>
    /// <param name="cancellationToken">Propagated to the underlying sign-in manager.</param>
    Task<StrataraSignInResult> TwoFactorRecoveryCodeSignInAsync(string recoveryCode, CancellationToken cancellationToken = default);

    /// <summary>Signs the current user out and clears any persisted token state.</summary>
    /// <param name="cancellationToken">Propagated to the underlying sign-in manager.</param>
    Task SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the currently issued <see cref="AccessTokenInfo"/>, refreshing the token transparently if needed.</summary>
    /// <param name="cancellationToken">Propagated to any token-refresh HTTP / DB call.</param>
    /// <returns>The token info, or <c>null</c> if no valid token is available.</returns>
    Task<AccessTokenInfo?> GetAccessTokenInfoAsync(CancellationToken cancellationToken = default);
}
