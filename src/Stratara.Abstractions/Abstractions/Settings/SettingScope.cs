namespace Stratara.Abstractions.Settings;

/// <summary>
/// Identifies which slice of the settings plane a value belongs to: global, per tenant, per
/// user, or per user-in-tenant. Mirrors the shape of the security plane's
/// <see cref="Stratara.Abstractions.Security.KeyScope"/> — ids are strings so opaque slugs and
/// GUIDs (via <c>Guid.ToString()</c>) work alike.
/// </summary>
/// <remarks>
/// Read/write operations address a scope <em>exactly</em>; only
/// <see cref="ISettingStore.DeleteScopeAsync"/> widens deliberately for erasure sweeps (see its
/// documentation). The read facade (<see cref="ISettingProvider"/>) walks scopes from most to
/// least specific: user-in-tenant → user → tenant → global.
/// </remarks>
/// <param name="TenantId">The owning tenant, or <c>null</c> for user-global/global scopes.</param>
/// <param name="UserId">The owning user, or <c>null</c> for tenant/global scopes.</param>
public readonly record struct SettingScope(string? TenantId = null, string? UserId = null)
{
    /// <summary>The application-wide scope (no tenant, no user).</summary>
    public static SettingScope Global => new();

    /// <summary>Scope for one tenant's settings.</summary>
    /// <param name="tenantId">The owning tenant.</param>
    /// <returns>The tenant scope.</returns>
    public static SettingScope ForTenant(Guid tenantId) => new(tenantId.ToString("D"));

    /// <summary>Scope for one user's tenant-independent settings.</summary>
    /// <param name="userId">The owning user.</param>
    /// <returns>The user scope.</returns>
    public static SettingScope ForUser(Guid userId) => new(null, userId.ToString("D"));

    /// <summary>Scope for one user's settings within one tenant.</summary>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="userId">The owning user.</param>
    /// <returns>The user-in-tenant scope.</returns>
    public static SettingScope ForUserInTenant(Guid tenantId, Guid userId) =>
        new(tenantId.ToString("D"), userId.ToString("D"));

    /// <summary>
    /// The canonical storage form of <see cref="TenantId"/>: the empty string when unset. Stores
    /// key rows by this form (never <c>NULL</c>) so composite keys stay portable across providers
    /// with differing NULL-uniqueness rules; owning it here keeps every store implementation and
    /// its test double on the same convention.
    /// </summary>
    public string NormalizedTenantId => TenantId ?? string.Empty;

    /// <summary>
    /// The canonical storage form of <see cref="UserId"/>: the empty string when unset.
    /// </summary>
    public string NormalizedUserId => UserId ?? string.Empty;
}
