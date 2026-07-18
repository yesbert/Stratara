using Microsoft.AspNetCore.Identity;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Session;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// Membership-backed <see cref="IAuthorizationProvider"/> spanning both role levels: a role
/// check passes when the current session's actor holds the role either as a tenant-scoped
/// membership role within the session's data-owner tenant <em>or</em> as a global ASP.NET
/// Identity role (the platform-role level — platform-administrator, developer and similar
/// roles that are not bound to any tenant).
/// </summary>
/// <remarks>
/// The membership level is consulted first; the global level via
/// <see cref="UserManager{TUser}"/> only on a membership miss, so tenant-scoped checks incur no
/// Identity-store round trip. Fail-closed: no ambient session or no matching role on either
/// level evaluates to <c>false</c>. The actor's user id is matched against the Identity user's
/// string key (ASP.NET Identity's default keys are GUID strings).
/// </remarks>
/// <typeparam name="TUser">The host's ASP.NET Identity user entity.</typeparam>
/// <param name="sessionContextProvider">Accessor for the ambient session (actor + data-owner tenant).</param>
/// <param name="membershipStore">The membership store the tenant-scoped roles are read from.</param>
/// <param name="userManager">The Identity user manager the global roles are read from.</param>
public sealed class MembershipAuthorizationProvider<TUser>(
    ISessionContextProvider sessionContextProvider,
    ITenantMembershipStore membershipStore,
    UserManager<TUser> userManager) : IAuthorizationProvider
    where TUser : class
{
    /// <inheritdoc/>
    public async Task<bool> IsInRoleAsync(string role, CancellationToken cancellationToken = default)
    {
        if (await MembershipRoleEvaluator.IsInMembershipRoleAsync(
                sessionContextProvider, membershipStore, role, cancellationToken))
        {
            return true;
        }

        var session = sessionContextProvider.Current;
        if (session is null)
        {
            return false;
        }

        var user = await userManager.FindByIdAsync(session.ActorUserId.ToString());
        return user is not null && await userManager.IsInRoleAsync(user, role);
    }
}
