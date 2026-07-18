using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Session;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// Shared membership-role check used by the membership-backed authorization providers:
/// does the current session's actor hold the role within the session's data-owner tenant?
/// </summary>
internal static class MembershipRoleEvaluator
{
    internal static async Task<bool> IsInMembershipRoleAsync(
        ISessionContextProvider sessionContextProvider,
        ITenantMembershipStore membershipStore,
        string role,
        CancellationToken cancellationToken)
    {
        var session = sessionContextProvider.Current;
        if (session is null)
        {
            return false;
        }

        var membership = await membershipStore.GetMembershipAsync(
            session.ActorUserId, session.TenantId, cancellationToken);

        return membership is { Status: MembershipStatus.Active }
               && membership.Roles.Contains(role, StringComparer.Ordinal);
    }
}
