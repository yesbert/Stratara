namespace Stratara.Abstractions.Multitenancy;

/// <summary>
/// One user's membership in one tenant, carrying the tenant-scoped roles the user holds there.
/// The user↔tenant relationship is many-to-many: a user may hold any number of memberships,
/// each with its own role set, and single-tenant applications are simply the degenerate case
/// of one membership per user.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Roles"/> are <em>tenant-scoped</em> role names (for example a tenant-administrator
/// role) that apply only within <see cref="TenantId"/>. They are independent of any global
/// platform roles the identity system may manage elsewhere (such as ASP.NET Identity's role
/// store); authorization components that consume memberships are expected to consult both levels.
/// </para>
/// <para>
/// A membership with <see cref="MembershipStatus.Pending"/> status represents an invitation that
/// has not been accepted yet and confers no access; one with <see cref="MembershipStatus.Former"/>
/// status records a member who has left and confers no access either.
/// </para>
/// </remarks>
/// <param name="UserId">The user who holds the membership.</param>
/// <param name="TenantId">The tenant the membership belongs to.</param>
/// <param name="Roles">
/// Tenant-scoped role names the user holds within <paramref name="TenantId"/>. Never
/// <c>null</c>; empty when the user is a plain member without elevated tenant roles.
/// </param>
/// <param name="Status">Lifecycle state of the membership; defaults to <see cref="MembershipStatus.Active"/>.</param>
public sealed record TenantMembership(
    Guid UserId,
    Guid TenantId,
    IReadOnlyCollection<string> Roles,
    MembershipStatus Status = MembershipStatus.Active)
{
    /// <summary>
    /// Returns this membership with <paramref name="roles"/> added to <see cref="Roles"/>. The roles already held keep
    /// their order, the added ones follow in the order given, and a role already held or named twice is held once.
    /// Roles are compared ordinally. The status is unchanged.
    /// </summary>
    /// <param name="roles">The role names to add.</param>
    /// <returns>The changed membership, or this instance when every role is already held.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="roles"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">A role name is <c>null</c>, empty or whitespace.</exception>
    public TenantMembership WithRolesAdded(IReadOnlyCollection<string> roles)
    {
        ValidateRoleNames(roles);
        var added = roles.Distinct(StringComparer.Ordinal).Where(role => !Roles.Contains(role, StringComparer.Ordinal)).ToList();
        return added.Count == 0 ? this : this with { Roles = [.. Roles, .. added] };
    }

    /// <summary>
    /// Returns this membership with <paramref name="roles"/> removed from <see cref="Roles"/>. The remaining roles keep
    /// their order; a role not held is ignored. Roles are compared ordinally. The status is unchanged, and removing
    /// every role leaves a membership without roles rather than no membership.
    /// </summary>
    /// <param name="roles">The role names to remove.</param>
    /// <returns>The changed membership, or this instance when none of the roles is held.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="roles"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">A role name is <c>null</c>, empty or whitespace.</exception>
    public TenantMembership WithRolesRemoved(IReadOnlyCollection<string> roles)
    {
        ValidateRoleNames(roles);
        var remaining = Roles.Where(role => !roles.Contains(role, StringComparer.Ordinal)).ToList();
        return remaining.Count == Roles.Count ? this : this with { Roles = remaining };
    }

    private static void ValidateRoleNames(IReadOnlyCollection<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        if (roles.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A role name must not be null, empty or whitespace.", nameof(roles));
        }
    }
}
