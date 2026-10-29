namespace Stratara.Abstractions.Multitenancy;

/// <summary>
/// Lifecycle state of a <see cref="TenantMembership"/>.
/// </summary>
/// <remarks>
/// Only <see cref="Active"/> memberships grant access: the membership-backed authorization
/// components (role checks, cross-tenant authorization, sign-in tenant resolution) ignore
/// memberships in any other state. <see cref="Pending"/> models an invitation that has been
/// issued but not yet accepted — the row already exists so the invitation can be listed and
/// accepted transactionally, but it confers no rights until it is activated. <see cref="Former"/>
/// records a member who has left: the row stays so the tenant still knows the user belonged to it,
/// and it confers nothing. Neither non-active state can be selected as the user's active tenant, and
/// writing one over a selected membership clears the selection.
/// </remarks>
public enum MembershipStatus
{
    /// <summary>The user is a full member of the tenant; roles on the membership apply.</summary>
    Active = 0,

    /// <summary>
    /// The membership has been offered (invitation) but not yet accepted; it confers no access.
    /// </summary>
    Pending = 1,

    /// <summary>
    /// The user was a member and no longer is; it confers no access and is kept until the user's or
    /// the tenant's erasure removes it.
    /// </summary>
    Former = 2,
}
