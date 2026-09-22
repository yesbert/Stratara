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
/// <para>
/// A configured role is recognised where the actor holds it: in the actor's own membership, or
/// through the registered role checker, which is where a global platform role is found. An actor
/// whose only role level is its membership — a machine actor holds no global roles — therefore
/// passes by a configured role too.
/// </para>
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

        if (options.CrossTenantRoles.Count == 0)
        {
            return false;
        }

        // Read once, and only where it can decide something: an actor operating on its own tenant has
        // already been answered above, and a role the provider grants costs no membership lookup at all.
        TenantMembership? atHome = null;
        var readAtHome = session.ActorTenantId == session.TenantId;

        foreach (var role in options.CrossTenantRoles)
        {
            if (await authorizationProvider.IsInRoleAsync(role, cancellationToken))
            {
                return true;
            }

            if (!readAtHome)
            {
                atHome = await membershipStore.GetMembershipAsync(
                    session.ActorUserId, session.ActorTenantId, cancellationToken);
                readAtHome = true;
            }

            if (MembershipRoleEvaluator.Carries(atHome, role))
            {
                return true;
            }
        }

        return false;
    }
}
