using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Session;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// Shared membership-role check used by the membership-backed authorization providers:
/// does the current session's actor hold the role within the session's data-owner tenant, or —
/// for a role the host named as resolving at home — within the actor's own tenant?
/// </summary>
internal static class MembershipRoleEvaluator
{
    internal static async Task<bool> IsInMembershipRoleAsync(
        ISessionContextProvider sessionContextProvider,
        ITenantMembershipStore membershipStore,
        string role,
        MembershipAuthorizationOptions? options,
        CancellationToken cancellationToken)
    {
        var session = sessionContextProvider.Current;
        if (session is null)
        {
            return false;
        }

        var membership = await membershipStore.GetMembershipAsync(
            session.ActorUserId, session.TenantId, cancellationToken);

        if (Carries(membership, role))
        {
            return true;
        }

        if (session.ActorTenantId == session.TenantId
            || options is null
            || !options.HomeTenantRoles.Contains(role))
        {
            return false;
        }

        var atHome = await membershipStore.GetMembershipAsync(
            session.ActorUserId, session.ActorTenantId, cancellationToken);

        return Carries(atHome, role);
    }

    internal static bool Carries(TenantMembership? membership, string role) =>
        membership is { Status: MembershipStatus.Active }
        && membership.Roles.Contains(role, StringComparer.Ordinal);
}
