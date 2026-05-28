using System.Diagnostics.CodeAnalysis;

namespace Stratara.Identity.Core.Models;

/// <summary>
/// Channel-agnostic sign-in outcome carrying the user-friendly failure message, the issued
/// <see cref="Models.AccessTokenInfo"/> on success, and the resolved user identity. Returned by both
/// <see cref="Abstractions.IStrataraSignInManager"/> and <see cref="Abstractions.IStrataraAuthenticationStateProvider"/>.
/// </summary>
/// <remarks>
/// Standalone type with no inheritance from <c>Microsoft.AspNetCore.Identity.SignInResult</c>, so hosts that
/// cannot or will not depend on ASP.NET Core (mobile, desktop, console, unit tests against the
/// abstraction surface) can consume this result type without pulling the MS Identity stack as a
/// transitive dependency.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class StrataraSignInResult
{
    /// <summary>Initializes a new instance carrying the outcome of a sign-in attempt.</summary>
    /// <param name="succeeded">Whether the sign-in succeeded.</param>
    /// <param name="loginFailureMessage">Human-readable failure message (already localized for the consumer) or <c>null</c> on success.</param>
    /// <param name="accessTokenInfo">Issued access-token information on success; <c>null</c> on failure.</param>
    /// <param name="userId">Resolved user id when the user could be looked up, even on partial-success outcomes (lockout, two-factor required).</param>
    /// <param name="isLockedOut">Whether the account is locked out due to repeated failures.</param>
    /// <param name="requiresTwoFactor">Whether the account requires a second-factor step to complete sign-in.</param>
    /// <param name="isNotAllowed">
    /// Whether the account exists but is not currently allowed to sign in (e.g. email not confirmed).
    /// Surfaced so trusted-context UIs (admin panels, internal tools) can branch on it. <strong>Do not</strong>
    /// reflect this flag in UI text exposed to anonymous users — combined with the generic
    /// <see cref="LoginFailureMessage"/> the framework collapses "wrong password" and "not allowed" into
    /// one user-visible outcome to prevent username enumeration.
    /// </param>
    public StrataraSignInResult(
        bool succeeded,
        string? loginFailureMessage,
        AccessTokenInfo? accessTokenInfo,
        Guid? userId = null,
        bool isLockedOut = false,
        bool requiresTwoFactor = false,
        bool isNotAllowed = false)
    {
        Succeeded = succeeded;
        LoginFailureMessage = loginFailureMessage;
        AccessTokenInfo = accessTokenInfo;
        UserId = userId;
        IsLockedOut = isLockedOut;
        RequiresTwoFactor = requiresTwoFactor;
        IsNotAllowed = isNotAllowed;
    }

    /// <summary>Whether the sign-in attempt succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Whether the account is locked out due to repeated failures.</summary>
    public bool IsLockedOut { get; init; }

    /// <summary>Whether the account requires a second-factor step to complete sign-in.</summary>
    public bool RequiresTwoFactor { get; init; }

    /// <summary>Human-readable failure message localized for display; <c>null</c> on success.</summary>
    public string? LoginFailureMessage { get; init; }

    /// <summary>Issued access-token info on a successful sign-in; <c>null</c> on failure.</summary>
    /// <remarks>
    /// Contains bearer credentials — see the security remarks on <see cref="Models.AccessTokenInfo"/>
    /// and <see cref="Models.LoginResponse"/>. Callers must persist this only in encrypted /
    /// platform-secure storage and must never log the value or surface it through telemetry.
    /// </remarks>
    public AccessTokenInfo? AccessTokenInfo { get; init; }

    /// <summary>Identifier of the user the sign-in attempt resolved to, when available.</summary>
    public Guid? UserId { get; init; }

    /// <summary>
    /// Whether the account exists but is currently not allowed to sign in. Internal-only signal — must not
    /// be reflected in anonymous-user UI text or the framework's username-enumeration mitigation is undone.
    /// </summary>
    public bool IsNotAllowed { get; init; }
}
