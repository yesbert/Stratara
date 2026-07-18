namespace Stratara.Abstractions.Multitenancy;

/// <summary>
/// Lifecycle state of a <see cref="TenantMembership"/>.
/// </summary>
/// <remarks>
/// Only <see cref="Active"/> memberships grant access: the membership-backed authorization
/// components (role checks, cross-tenant authorization, sign-in tenant resolution) ignore
/// memberships in any other state. <see cref="Pending"/> models an invitation that has been
/// issued but not yet accepted — the row already exists so the invitation can be listed and
/// accepted transactionally, but it confers no rights until it is activated.
/// </remarks>
public enum MembershipStatus
{
    /// <summary>The user is a full member of the tenant; roles on the membership apply.</summary>
    Active = 0,

    /// <summary>
    /// The membership has been offered (invitation) but not yet accepted; it confers no access.
    /// </summary>
    Pending = 1,
}
