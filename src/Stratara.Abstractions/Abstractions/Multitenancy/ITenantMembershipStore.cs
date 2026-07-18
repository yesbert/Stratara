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
    /// Gets every membership of the tenant — the reverse lookup ("who belongs to this tenant").
    /// </summary>
    /// <param name="tenantId">The tenant whose members to load.</param>
    /// <param name="cancellationToken">Token to observe while loading.</param>
    /// <returns>The tenant's memberships; empty when the tenant has no members.</returns>
    Task<IReadOnlyList<TenantMembership>> GetMembersAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or replaces the membership identified by the record's
    /// <see cref="TenantMembership.UserId"/> and <see cref="TenantMembership.TenantId"/> (upsert).
    /// </summary>
    /// <param name="membership">The membership to persist; its role set and status replace any existing values.</param>
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
