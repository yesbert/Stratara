using System.Security.Claims;
using Stratara.Abstractions.Multitenancy;
using Stratara.Sessions.Multitenancy;

namespace Stratara.Identity.AspNetCore.Services;

/// <summary>
/// Shared tenant-resolution logic for the sign-in claims bridge: which tenant should the
/// <c>stratara:tenant_id</c> claim carry for a given user, based on the user's memberships and
/// the persisted active-tenant selection.
/// </summary>
internal static class MembershipTenantClaimResolver
{
    /// <summary>
    /// Resolves the tenant to stamp: the user's persisted active-tenant selection when it points at
    /// an active membership, otherwise their only active membership where they hold exactly one.
    /// <c>null</c> when the user holds no active membership, and equally when they hold several and
    /// have not chosen between them — the framework does not choose for them, because a wrong tenant
    /// claim fails invisibly where a missing one fails closed.
    /// </summary>
    internal static async Task<Guid?> ResolveTenantAsync(
        ITenantMembershipStore membershipStore, Guid userId, CancellationToken cancellationToken)
    {
        var memberships = await membershipStore.GetMembershipsAsync(userId, cancellationToken);
        var active = memberships
            .Where(m => m.Status == MembershipStatus.Active)
            .ToList();

        if (active.Count == 0)
        {
            return null;
        }

        var selection = await membershipStore.GetActiveTenantAsync(userId, cancellationToken);
        if (selection is { } selected && active.Any(m => m.TenantId == selected))
        {
            return selected;
        }

        return active.Count == 1 ? active[0].TenantId : null;
    }

    /// <summary>
    /// Reads the user id from the principal's <see cref="ClaimTypes.NameIdentifier"/> claim.
    /// </summary>
    internal static Guid? GetUserId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? userId
            : null;

    /// <summary>
    /// Reads the tenant id from the principal's <c>stratara:tenant_id</c> claim.
    /// </summary>
    internal static Guid? GetTenantId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(StrataraClaimTypes.TenantId), out var tenantId)
            ? tenantId
            : null;

    /// <summary>
    /// Stamps the <c>stratara:tenant_id</c> claim onto the principal's primary identity — the
    /// single place that owns the claim's encoding (round-trippable <c>"D"</c> Guid format).
    /// </summary>
    internal static void StampTenantClaim(ClaimsPrincipal principal, Guid tenantId)
    {
        if (principal.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim(StrataraClaimTypes.TenantId, tenantId.ToString("D")));
        }
    }
}
