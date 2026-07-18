using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Stratara.Abstractions.Multitenancy;
using Stratara.Sessions.Multitenancy;

namespace Stratara.Identity.AspNetCore.Services;

/// <summary>
/// Decorates the host's <see cref="IUserClaimsPrincipalFactory{TUser}"/> and stamps the
/// <c>stratara:tenant_id</c> claim from the user's tenant membership onto every principal the
/// factory issues — the sign-in bridge between the identity plane and Stratara's session-context
/// middleware, which reads exactly that claim.
/// </summary>
/// <remarks>
/// <para>
/// Tenant resolution: the user's persisted active-tenant selection when it points at an active
/// membership, otherwise the user's only — or deterministically first — active membership. A
/// user without any active membership gets no claim, so downstream tenant resolution stays on
/// its fail-closed path. A principal that already carries the claim (a consumer-owned factory
/// stamped it) is left untouched.
/// </para>
/// <para>
/// The claim is stamped wherever the factory runs — cookie issuance and ASP.NET Identity bearer
/// tokens alike. Principals minted outside the factory (application-owned machine JWTs) stamp
/// the claim themselves.
/// </para>
/// </remarks>
/// <typeparam name="TUser">The host's ASP.NET Identity user entity.</typeparam>
/// <param name="inner">The decorated factory that builds the base principal.</param>
/// <param name="membershipStore">The membership store the tenant is resolved from.</param>
public sealed class MembershipClaimsPrincipalFactory<TUser>(
    IUserClaimsPrincipalFactory<TUser> inner,
    ITenantMembershipStore membershipStore) : IUserClaimsPrincipalFactory<TUser>
    where TUser : class
{
    /// <inheritdoc/>
    public async Task<ClaimsPrincipal> CreateAsync(TUser user)
    {
        var principal = await inner.CreateAsync(user);

        if (principal.HasClaim(claim => claim.Type == StrataraClaimTypes.TenantId))
        {
            return principal;
        }

        if (MembershipTenantClaimResolver.GetUserId(principal) is not { } userId)
        {
            return principal;
        }

        var tenantId = await MembershipTenantClaimResolver.ResolveTenantAsync(
            membershipStore, userId, CancellationToken.None);

        if (tenantId is { } resolved)
        {
            MembershipTenantClaimResolver.StampTenantClaim(principal, resolved);
        }

        return principal;
    }
}
