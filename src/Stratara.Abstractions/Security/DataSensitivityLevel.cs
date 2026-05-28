namespace Stratara.Abstractions.Security;

/// <summary>
/// Sensitivity tier for fields protected by <see cref="EncryptDataAttribute"/>.
/// Controls key scoping in <see cref="Stratara.Abstractions.Security.IKeyStore"/>.
/// </summary>
public enum DataSensitivityLevel
{
    /// <summary>Field is not encrypted at all.</summary>
    None,
    /// <summary>Key is scoped to a specific user — DSGVO Art. 17 crypto-shred via user-key revocation.</summary>
    UserScoped,
    /// <summary>Key is scoped to a tenant — all data of a tenant uses the same key.</summary>
    TenantScoped,
    /// <summary>Key is scoped to a single Confidential tier — system-wide key, treated as the highest sensitivity.</summary>
    Confidential
}
