using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

namespace Stratara.Identity.AspNetCore.Authentication;

/// <summary>
/// The facts a JIT external-login provisioning attempt is evaluated against — passed to the
/// optional invitation gate so a host can accept or reject a first-time external sign-in based on a
/// pending invitation, an allow-list, or any other policy.
/// </summary>
/// <param name="LoginProvider">The external scheme/issuer (for example <c>OpenIdConnect</c>, <c>Entra</c>).</param>
/// <param name="ProviderKey">The issuer's stable subject identifier (<c>sub</c>) — the account is linked on this, never on email.</param>
/// <param name="Email">The email the provider asserted, or <c>null</c> when none was present.</param>
/// <param name="EmailVerifiedByProvider">Whether the provider asserted the email as verified (<c>email_verified</c>/<c>xms_edov</c>).</param>
/// <param name="Principal">The full external principal, for any additional claim the gate needs.</param>
[ExcludeFromCodeCoverage]
public sealed record ExternalLoginProvisioningContext(
    string LoginProvider,
    string ProviderKey,
    string? Email,
    bool EmailVerifiedByProvider,
    ClaimsPrincipal Principal);
