using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Stratara.Abstractions.Multitenancy;
using Stratara.Sessions.Multitenancy;

namespace Stratara.Identity.AspNetCore.Services;

/// <summary>
/// Per-request alternative to <see cref="MembershipClaimsPrincipalFactory{TUser}"/>: an
/// <see cref="IClaimsTransformation"/> that resolves the <c>stratara:tenant_id</c> claim from
/// the membership store on every request instead of stamping it into the issued
/// cookie or token.
/// </summary>
/// <remarks>
/// Choose this mode when a tenant switch must take effect immediately without re-issuing the
/// sign-in (the selection is read live per request), at the cost of a membership-store lookup
/// per request — put caching behind the store when that lookup gets hot. Idempotent: principals
/// that already carry the claim (stamped at issuance or by an earlier transformation run) pass
/// through unchanged, so it composes with the factory mode and with consumer-minted tokens.
/// </remarks>
/// <param name="membershipStore">The membership store the tenant is resolved from.</param>
public sealed class MembershipClaimsTransformation(
    ITenantMembershipStore membershipStore) : IClaimsTransformation
{
    /// <inheritdoc/>
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true
            || principal.HasClaim(claim => claim.Type == StrataraClaimTypes.TenantId))
        {
            return principal;
        }

        if (MembershipTenantClaimResolver.GetUserId(principal) is not { } userId)
        {
            return principal;
        }

        var tenantId = await MembershipTenantClaimResolver.ResolveTenantAsync(
            membershipStore, userId, CancellationToken.None);

        if (tenantId is not { } resolved)
        {
            return principal;
        }

        var clone = principal.Clone();
        MembershipTenantClaimResolver.StampTenantClaim(clone, resolved);
        return clone;
    }
}
