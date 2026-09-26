namespace Stratara.Abstractions.Security;

/// <summary>
/// Sensitivity tier for fields protected by <see cref="EncryptDataAttribute"/>.
/// Controls key scoping in <see cref="Stratara.Abstractions.Security.IKeyStore"/>.
/// </summary>
public enum DataSensitivityLevel
{
    /// <summary>Field is not encrypted at all.</summary>
    None,
    /// <summary>
    /// Key is scoped to the data owner's user within its tenant, or to the tenant alone when no user
    /// is set. Shredded by the user's erasure and by the tenant's.
    /// </summary>
    UserScoped,
    /// <summary>
    /// Key is scoped to the tenant, together with the data owner's user where one is set. Shredded by
    /// the tenant's erasure; a user's erasure leaves it.
    /// </summary>
    TenantScoped,
    /// <summary>
    /// Highest sensitivity, claiming no per-subject isolation. Written with an empty tenant, it uses
    /// one system-wide key that no erasure shreds; written for a tenant, it uses a key of that tenant,
    /// shredded by the tenant's erasure.
    /// </summary>
    Confidential
}
