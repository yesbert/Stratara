using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Multitenancy;
using Stratara.Contracts.Session;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// Membership-backed <see cref="ICrossTenantAuthorizer"/> for strict tenant isolation: a
/// cross-tenant operation (actor tenant ≠ data-owner tenant) is allowed when the actor holds an
/// active membership in the data-owner tenant, or when the actor holds one of the configured
/// <see cref="MembershipCrossTenantAuthorizerOptions.CrossTenantRoles"/> (the
/// operator-impersonation path for platform administrators, who typically hold no membership in
/// the tenants they administer).
/// </summary>
/// <remarks>
/// Replaces the framework's deny-all default with stored facts. Register it via
/// <c>AddMembershipCrossTenantAuthorizer(...)</c> <em>alongside</em>
/// <c>AddStrataraTenantIsolation(o =&gt; o.Mode = TenantIsolationMode.Strict)</c>; without a
/// membership and without a configured role the authorizer stays fail-closed.
/// </remarks>
/// <param name="membershipStore">The membership store consulted for the actor's membership in the data-owner tenant.</param>
/// <param name="authorizationProvider">The role checker consulted for the configured cross-tenant roles.</param>
/// <param name="options">The configured cross-tenant roles.</param>
public sealed class MembershipCrossTenantAuthorizer(
    ITenantMembershipStore membershipStore,
    IAuthorizationProvider authorizationProvider,
    MembershipCrossTenantAuthorizerOptions options) : ICrossTenantAuthorizer
{
    /// <inheritdoc/>
    public async ValueTask<bool> IsCrossTenantAllowedAsync(
        SessionContext session, CancellationToken cancellationToken = default)
    {
        var membership = await membershipStore.GetMembershipAsync(
            session.ActorUserId, session.TenantId, cancellationToken);

        if (membership is { Status: MembershipStatus.Active })
        {
            return true;
        }

        foreach (var role in options.CrossTenantRoles)
        {
            if (await authorizationProvider.IsInRoleAsync(role, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
