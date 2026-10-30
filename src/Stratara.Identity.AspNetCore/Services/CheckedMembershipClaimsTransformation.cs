using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Stratara.Abstractions.Multitenancy;

namespace Stratara.Identity.AspNetCore.Services;

/// <summary>
/// <see cref="IClaimsTransformation"/> of the kept-and-checked tenant-claim mode: on every request it
/// verifies that the user's membership in the tenant the principal carries is still
/// <see cref="MembershipStatus.Active"/>, and removes the tenant claim where it is not.
/// </summary>
/// <remarks>
/// <para>
/// Only a principal ASP.NET Identity issued — the one the claims factory stamped, carried by an
/// Identity cookie or bearer token — is checked. A principal of another scheme passes through
/// unchanged: an API key is validated against its own store, and a token your application mints
/// carries the tenant it decided on.
/// </para>
/// <para>
/// A principal without the tenant claim passes through unchanged: this mode never resolves a tenant
/// per request, so a selection moved elsewhere — by another application sharing the identity store —
/// does not move this sign-in. A removed claim is not replaced by another of the user's tenants; the
/// request resolves as one without a claim does, to the reserved default tenant. A principal carrying
/// the claim whose user id cannot be read loses the claim as well: a membership that cannot be looked
/// up cannot be verified.
/// </para>
/// <para>
/// The verdict is kept for the request, so a request authenticated more than once looks the
/// membership up once.
/// </para>
/// </remarks>
/// <param name="membershipStore">The membership store the carried tenant is checked against.</param>
public sealed class CheckedMembershipClaimsTransformation(
    ITenantMembershipStore membershipStore) : IClaimsTransformation
{
    private readonly Dictionary<(string? User, Guid Tenant), bool> _verdicts = [];

    /// <inheritdoc/>
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true
            || !principal.Identities.Any(identity => identity.AuthenticationType == IdentityConstants.ApplicationScheme)
            || MembershipTenantClaimResolver.GetTenantId(principal) is not { } tenantId)
        {
            return principal;
        }

        var key = (principal.FindFirstValue(ClaimTypes.NameIdentifier), tenantId);
        if (!_verdicts.TryGetValue(key, out var isActiveMember))
        {
            isActiveMember = await MembershipTenantClaimResolver.IsActiveMemberAsync(
                membershipStore, principal, tenantId, CancellationToken.None);
            _verdicts[key] = isActiveMember;
        }

        if (isActiveMember)
        {
            return principal;
        }

        var withoutTenant = new ClaimsPrincipal(principal.Identities.Select(identity => identity.Clone()));
        MembershipTenantClaimResolver.RemoveTenantClaims(withoutTenant);
        return withoutTenant;
    }
}
