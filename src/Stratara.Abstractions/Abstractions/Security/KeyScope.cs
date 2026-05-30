namespace Stratara.Abstractions.Security;

/// <summary>
/// Identifies the scope a data-encryption key belongs to: a <see cref="DataSensitivityLevel"/>
/// optionally narrowed to a tenant and/or user. The <see cref="IKeyStore"/> derives a stable
/// key id from this scope.
/// </summary>
/// <remarks>
/// <see cref="TenantId"/> and <see cref="UserId"/> are <see langword="string"/> (not <see cref="System.Guid"/>)
/// so the scope carries both opaque slug identifiers and GUID values (via <c>Guid.ToString()</c>)
/// without loss.
/// </remarks>
/// <param name="Level">The sensitivity tier the key protects.</param>
/// <param name="TenantId">The owning tenant, or <see langword="null"/> for tenant-agnostic keys.</param>
/// <param name="UserId">The owning user, or <see langword="null"/> for non-user-scoped keys.</param>
public readonly record struct KeyScope(DataSensitivityLevel Level, string? TenantId = null, string? UserId = null);
