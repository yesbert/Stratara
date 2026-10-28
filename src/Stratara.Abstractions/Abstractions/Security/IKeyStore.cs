namespace Stratara.Abstractions.Security;

/// <summary>
/// Manages versioned data-encryption keys (DEKs) used by the framework's
/// <see cref="IEncryptionFactory"/> + <see cref="ISecureBlobEncryptor"/> +
/// <see cref="ISecureJsonSerializer"/>. Keys are scoped by <see cref="KeyScope"/>
/// (<see cref="DataSensitivityLevel"/> + optional tenant / user) and versioned, so rotation
/// keeps older ciphertext decryptable while <see cref="RevokeAsync"/> /
/// <see cref="EraseScopeAsync"/> implement GDPR Art. 17 crypto-shredding, and
/// <see cref="MarkSubjectErasedAsync"/> keeps an erased subject from being given a key again.
/// Revoking a version marks nothing: a scope whose every version was revoked is given a new one.
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
    /// <exception cref="KeyScopeErasedException">
    /// The scope names a subject marked erased through <see cref="MarkSubjectErasedAsync"/>; nothing is created.
    /// </exception>
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
    /// <exception cref="KeyScopeErasedException">
    /// The scope names a subject marked erased through <see cref="MarkSubjectErasedAsync"/>; nothing is created.
    /// </exception>
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

    /// <summary>
    /// List the scope of every key the store holds. An erasure uses it to find every key that names
    /// a subject, including a key that also names a subject no longer in the directory.
    /// </summary>
    /// <remarks>
    /// The default implementation throws <see cref="NotSupportedException"/>, and an erasure then
    /// shreds only the scopes the directory names. Those miss a key shared with a former member, or
    /// with an operator acting in a tenant from outside it, so a key store should implement this.
    /// </remarks>
    /// <param name="cancellationToken">Propagated to the underlying store.</param>
    /// <returns>Every scope that holds at least one key version; an erased scope is not listed.</returns>
    /// <exception cref="NotSupportedException">The store cannot list its scopes.</exception>
    ValueTask<IReadOnlyList<KeyScope>> ListScopesAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} cannot list its key scopes.");

    /// <summary>
    /// Record that a subject has been erased, so that the store creates no key for it again: from then
    /// on <see cref="GetOrCreateCurrentKeyAsync"/> and <see cref="RotateAsync"/> throw
    /// <see cref="KeyScopeErasedException"/> for every scope the subject <see cref="KeySubject.Covers"/>,
    /// whether or not it ever held a key. The mark is permanent, and marking a subject twice changes
    /// nothing.
    /// </summary>
    /// <remarks>
    /// An erasure marks the subject before it lists and shreds its keys, so an encryption racing it either
    /// creates its key first — and that key is shredded with the rest — or is refused. The default
    /// implementation throws <see cref="NotSupportedException"/>, and an erasure then warns that keys for
    /// the subject can be created again, so a key store should implement this.
    /// </remarks>
    /// <param name="subject">The erased tenant or user.</param>
    /// <param name="cancellationToken">Propagated to the underlying store.</param>
    /// <exception cref="NotSupportedException">The store cannot mark a subject erased.</exception>
    /// <exception cref="ArgumentException"><paramref name="subject"/> is the default value, which names no subject.</exception>
    ValueTask MarkSubjectErasedAsync(KeySubject subject, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} cannot mark a subject erased.");
}
