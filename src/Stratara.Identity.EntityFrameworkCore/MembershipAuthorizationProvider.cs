using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Session;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// Membership-backed <see cref="IAuthorizationProvider"/>: a role check passes when the current
/// session's actor holds the role as a tenant-scoped membership role within the session's
/// data-owner tenant. Use this overload for hosts without a global (ASP.NET Identity) role
/// store; hosts that also gate on global platform roles use
/// <see cref="MembershipAuthorizationProvider{TUser}"/> instead.
/// </summary>
/// <remarks>
/// Fail-closed: no ambient session, no membership, a non-active membership, or an unknown role
/// all evaluate to <c>false</c>. Register it as the provider behind the authorizing mediator
/// (for example <c>AddAuthorizingMediator&lt;MembershipAuthorizationProvider&gt;()</c>) or via
/// <c>AddMembershipAuthorization()</c> for hosts that resolve
/// <see cref="IAuthorizationProvider"/> directly (such as the authorizing outbox dispatcher).
/// </remarks>
/// <param name="sessionContextProvider">Accessor for the ambient session (actor + data-owner tenant).</param>
/// <param name="membershipStore">The membership store the roles are read from.</param>
public sealed class MembershipAuthorizationProvider(
    ISessionContextProvider sessionContextProvider,
    ITenantMembershipStore membershipStore) : IAuthorizationProvider
{
    /// <inheritdoc/>
    public Task<bool> IsInRoleAsync(string role, CancellationToken cancellationToken = default) =>
        MembershipRoleEvaluator.IsInMembershipRoleAsync(
            sessionContextProvider, membershipStore, role, cancellationToken);
}
