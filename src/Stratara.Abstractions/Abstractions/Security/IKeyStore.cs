namespace Stratara.Abstractions.Security;

/// <summary>
/// Manages versioned data-encryption keys (DEKs) used by the framework's
/// <see cref="IEncryptionFactory"/> + <see cref="ISecureBlobEncryptor"/> +
/// <see cref="ISecureJsonSerializer"/>. Keys are scoped by <see cref="KeyScope"/>
/// (<see cref="DataSensitivityLevel"/> + optional tenant / user) and versioned, so rotation
/// keeps older ciphertext decryptable while <see cref="RevokeAsync"/> /
/// <see cref="EraseScopeAsync"/> implement GDPR Art. 17 crypto-shredding.
/// </summary>
public interface IKeyStore
{
    /// <summary>
    /// Return the current (highest non-revoked version) key for the scope, creating a first
    /// version if none exists yet.
    /// </summary>
    /// <param name="scope">The key scope to resolve.</param>
    /// <param name="cancellationToken">Propagated to the underlying store.</param>
    /// <returns>The resolved <see cref="KeyMaterial"/> (key id + raw bytes).</returns>
    ValueTask<KeyMaterial> GetOrCreateCurrentKeyAsync(KeyScope scope, CancellationToken cancellationToken = default);

    /// <summary>Return the raw key bytes for the id, or <see langword="null"/> if revoked / erased / unknown.</summary>
    /// <param name="keyId">The key id previously obtained from <see cref="GetOrCreateCurrentKeyAsync"/>.</param>
    /// <param name="cancellationToken">Propagated to the underlying store.</param>
    ValueTask<byte[]?> GetDataEncryptionKeyAsync(string keyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a new key version for the scope and make it the current one. Older versions remain
    /// resolvable via <see cref="GetDataEncryptionKeyAsync"/> so existing ciphertext stays readable.
    /// </summary>
    /// <param name="scope">The key scope to rotate.</param>
    /// <param name="cancellationToken">Propagated to the underlying store.</param>
    /// <returns>The id of the newly created current key.</returns>
    ValueTask<string> RotateAsync(KeyScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently revoke a single key version. After revocation
    /// <see cref="GetDataEncryptionKeyAsync"/> returns <see langword="null"/> for that id — the
    /// ciphertext encrypted under it becomes undecryptable (crypto-shred of one version).
    /// </summary>
    /// <param name="keyId">The key id to revoke.</param>
    /// <param name="cancellationToken">Propagated to the underlying store.</param>
    ValueTask RevokeAsync(string keyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Erase every key version for the scope (GDPR Art. 17). All ciphertext under the scope
    /// becomes permanently undecryptable.
    /// </summary>
    /// <param name="scope">The key scope to erase.</param>
    /// <param name="cancellationToken">Propagated to the underlying store.</param>
    ValueTask EraseScopeAsync(KeyScope scope, CancellationToken cancellationToken = default);
}
