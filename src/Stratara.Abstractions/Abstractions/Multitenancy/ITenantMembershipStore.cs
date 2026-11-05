namespace Stratara.Abstractions.Multitenancy;

/// <summary>
/// Persistence contract for user↔tenant memberships (<see cref="TenantMembership"/>) — the
/// authorization-plane record of which tenants a user belongs to and which tenant-scoped roles
/// the user holds in each.
/// </summary>
/// <remarks>
/// <para>
/// The store is deliberately relational-shaped (read-your-write consistency at sign-in time),
/// but implementations are free to maintain the underlying data from domain events: a consumer
/// whose membership changes are event-sourced keeps emitting its events and updates the store
/// from a projection or saga instead of calling <see cref="SetMembershipAsync"/> inline.
/// </para>
/// <para>
/// Both directions of the relationship are first-class: <see cref="GetMembershipsAsync"/>
/// answers "which tenants may this user access" (sign-in tenant resolution, tenant switching)
/// and <see cref="GetMembersAsync"/> answers "who belongs to this tenant" (administration,
/// cascade cleanup). The sweep methods (<see cref="RemoveAllMembershipsAsync"/>,
/// <see cref="RemoveAllMembersAsync"/>) exist so user- and tenant-erasure flows can clear the
/// membership plane in one call.
/// </para>
/// </remarks>
public interface ITenantMembershipStore
{
    /// <summary>
    /// Gets every membership the user holds, regardless of status.
    /// </summary>
    /// <param name="userId">The user whose memberships to load.</param>
    /// <param name="cancellationToken">Token to observe while loading.</param>
    /// <returns>The user's memberships; empty when the user belongs to no tenant.</returns>
    Task<IReadOnlyList<TenantMembership>> GetMembershipsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the user's membership in one specific tenant.
    /// </summary>
    /// <param name="userId">The user whose membership to load.</param>
    /// <param name="tenantId">The tenant to look the membership up in.</param>
    /// <param name="cancellationToken">Token to observe while loading.</param>
    /// <returns>The membership, or <c>null</c> when the user does not belong to the tenant.</returns>
    Task<TenantMembership?> GetMembershipAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets every membership of the tenant, regardless of status — the reverse lookup ("who belongs
    /// to this tenant"), including pending and former members.
    /// </summary>
    /// <param name="tenantId">The tenant whose members to load.</param>
    /// <param name="cancellationToken">Token to observe while loading.</param>
    /// <returns>The tenant's memberships; empty when the tenant has no members.</returns>
    Task<IReadOnlyList<TenantMembership>> GetMembersAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or replaces the membership identified by the record's
    /// <see cref="TenantMembership.UserId"/> and <see cref="TenantMembership.TenantId"/> (upsert).
    /// </summary>
    /// <param name="membership">
    /// The membership to persist; its role set and status replace any existing values. A status other
    /// than <see cref="MembershipStatus.Active"/> clears the user's active-tenant selection where it
    /// points at this tenant.
    /// </param>
    /// <param name="cancellationToken">Token to observe while persisting.</param>
    Task SetMembershipAsync(TenantMembership membership, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the user's membership in one tenant. No-op when no such membership exists.
    /// </summary>
    /// <param name="userId">The user whose membership to remove.</param>
    /// <param name="tenantId">The tenant to remove the membership from.</param>
    /// <param name="cancellationToken">Token to observe while removing.</param>
    Task RemoveMembershipAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds roles to the user's membership in one tenant, leaving its other roles and its status as they are. Never
    /// creates a membership.
    /// </summary>
    /// <remarks>
    /// The framework's stores apply the change atomically: two role changes to the same membership that overlap both
    /// take effect. This default implementation reads the membership and writes it back whole with
    /// <see cref="SetMembershipAsync"/>, so on a store that does not override it a concurrent writer can undo the
    /// change; an implementation should override it, and a decorator should forward it to the store it wraps. A whole
    /// write through <see cref="SetMembershipAsync"/> still
    /// replaces the roles, so it and a role change running at the same time do not protect each other.
    /// </remarks>
    /// <param name="userId">The user whose membership to change.</param>
    /// <param name="tenantId">The tenant of the membership.</param>
    /// <param name="roles">The role names to add; see <see cref="TenantMembership.WithRolesAdded"/>.</param>
    /// <param name="cancellationToken">Token to observe while changing.</param>
    /// <returns>The membership as it stands afterwards, or <c>null</c> when the user does not belong to the tenant.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="roles"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// A role name is <c>null</c>, empty or whitespace and the membership exists; without a membership nothing is
    /// changed and <c>null</c> is returned.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The store could not apply the change safely: concurrent writers changed the membership's roles on every
    /// attempt, or the stored roles were written in a form the store cannot compare against.
    /// </exception>
    async Task<TenantMembership?> AddRolesAsync(
        Guid userId, Guid tenantId, IReadOnlyCollection<string> roles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var membership = await GetMembershipAsync(userId, tenantId, cancellationToken);
        if (membership is null)
        {
            return null;
        }

        var changed = membership.WithRolesAdded(roles);
        if (!ReferenceEquals(changed, membership))
        {
            await SetMembershipAsync(changed, cancellationToken);
        }

        return changed;
    }

    /// <summary>
    /// Removes roles from the user's membership in one tenant, leaving its other roles and its status as they are.
    /// Removing every role leaves a membership without roles; <see cref="RemoveMembershipAsync"/> removes the
    /// membership.
    /// </summary>
    /// <remarks>
    /// The framework's stores apply the change atomically: two role changes to the same membership that overlap both
    /// take effect. This default implementation reads the membership and writes it back whole with
    /// <see cref="SetMembershipAsync"/>, so on a store that does not override it a concurrent writer can undo the
    /// change; an implementation should override it, and a decorator should forward it to the store it wraps. A whole
    /// write through <see cref="SetMembershipAsync"/> still
    /// replaces the roles, so it and a role change running at the same time do not protect each other.
    /// </remarks>
    /// <param name="userId">The user whose membership to change.</param>
    /// <param name="tenantId">The tenant of the membership.</param>
    /// <param name="roles">The role names to remove; see <see cref="TenantMembership.WithRolesRemoved"/>.</param>
    /// <param name="cancellationToken">Token to observe while changing.</param>
    /// <returns>The membership as it stands afterwards, or <c>null</c> when the user does not belong to the tenant.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="roles"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// A role name is <c>null</c>, empty or whitespace and the membership exists; without a membership nothing is
    /// changed and <c>null</c> is returned.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The store could not apply the change safely: concurrent writers changed the membership's roles on every
    /// attempt, or the stored roles were written in a form the store cannot compare against.
    /// </exception>
    async Task<TenantMembership?> RemoveRolesAsync(
        Guid userId, Guid tenantId, IReadOnlyCollection<string> roles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var membership = await GetMembershipAsync(userId, tenantId, cancellationToken);
        if (membership is null)
        {
            return null;
        }

        var changed = membership.WithRolesRemoved(roles);
        if (!ReferenceEquals(changed, membership))
        {
            await SetMembershipAsync(changed, cancellationToken);
        }

        return changed;
    }

    /// <summary>
    /// Removes every membership the user holds — the membership-plane step of a user-erasure
    /// (GDPR) sweep. No-op when the user holds none.
    /// </summary>
    /// <param name="userId">The user whose memberships to remove.</param>
    /// <param name="cancellationToken">Token to observe while removing.</param>
    Task RemoveAllMembershipsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every membership of the tenant — the membership-plane step of a tenant-erasure
    /// sweep. No-op when the tenant has no members.
    /// </summary>
    /// <param name="tenantId">The tenant whose memberships to remove.</param>
    /// <param name="cancellationToken">Token to observe while removing.</param>
    Task RemoveAllMembersAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the tenant the user has explicitly selected as the active one for sign-in, or
    /// <c>null</c> when the user never made a selection (callers fall back to the user's only —
    /// or first — active membership).
    /// </summary>
    /// <param name="userId">The user whose selection to load.</param>
    /// <param name="cancellationToken">Token to observe while loading.</param>
    /// <returns>The selected tenant, or <c>null</c> when no explicit selection exists.</returns>
    Task<Guid?> GetActiveTenantAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the user's active-tenant selection — the tenant-switch operation for users with
    /// more than one membership. Implementations enforce the membership guard: the selection is
    /// rejected when the user holds no <see cref="MembershipStatus.Active"/> membership in the
    /// target tenant.
    /// </summary>
    /// <param name="userId">The user switching tenants.</param>
    /// <param name="tenantId">The tenant to make active.</param>
    /// <param name="cancellationToken">Token to observe while persisting.</param>
    /// <exception cref="InvalidOperationException">
    /// The user holds no active membership in <paramref name="tenantId"/>.
    /// </exception>
    Task SetActiveTenantAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken = default);
}
