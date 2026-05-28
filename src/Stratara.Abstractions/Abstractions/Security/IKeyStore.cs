using Stratara.Abstractions.Security;

namespace Stratara.Abstractions.Security;

/// <summary>
/// Manages data-encryption keys (DEKs) used by the framework's
/// <see cref="IEncryptionFactory"/> + <see cref="ISecureBlobEncryptor"/> +
/// <see cref="ISecureJsonSerializer"/>. Keys are scoped by
/// <see cref="DataSensitivityLevel"/>, tenant id and optionally user id.
/// </summary>
public interface IKeyStore
{
    /// <summary>
    /// Return the id of the key that matches the given scope, creating one if it does
    /// not exist yet.
    /// </summary>
    /// <param name="level">The sensitivity tier.</param>
    /// <param name="tenantId">Tenant scope, or <c>null</c> for tenant-agnostic keys.</param>
    /// <param name="userId">User scope, or <c>null</c> for non-user-scoped keys.</param>
    /// <param name="cancellationToken">Propagated to the underlying store.</param>
    ValueTask<string> EnsureKeyAsync(DataSensitivityLevel level, Guid? tenantId, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>Return the raw key bytes for the id, or <c>null</c> if revoked / unknown.</summary>
    ValueTask<byte[]?> GetDataEncryptionKeyAsync(string keyId, CancellationToken cancellationToken = default);

    /// <summary>Permanently revoke a key — implements crypto-shredding for DSGVO Art. 17.</summary>
    ValueTask RevokeAsync(string keyId, CancellationToken cancellationToken = default);
}
