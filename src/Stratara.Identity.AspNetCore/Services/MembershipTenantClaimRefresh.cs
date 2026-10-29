using Microsoft.AspNetCore.Identity;
using Stratara.Abstractions.Multitenancy;

namespace Stratara.Identity.AspNetCore.Services;

/// <summary>
/// The refresh half of the kept-and-checked tenant-claim mode: when the security-stamp validator
/// re-issues a cookie sign-in, the tenant the current principal carries replaces whatever the claims
/// factory resolved from the selection, as long as the membership in it is still active.
/// </summary>
internal static class MembershipTenantClaimRefresh
{
    internal static async Task KeepTenantAsync(
        SecurityStampRefreshingPrincipalContext context, ITenantMembershipStore? membershipStore, CancellationToken cancellationToken)
    {
        if (context.NewPrincipal is not { } refreshed)
        {
            return;
        }

        MembershipTenantClaimResolver.RemoveTenantClaims(refreshed);

        if (membershipStore is null
            || context.CurrentPrincipal is not { } current
            || MembershipTenantClaimResolver.GetTenantId(current) is not { } carried
            || !await MembershipTenantClaimResolver.IsActiveMemberAsync(membershipStore, refreshed, carried, cancellationToken))
        {
            return;
        }

        MembershipTenantClaimResolver.StampTenantClaim(refreshed, carried);
    }
}
