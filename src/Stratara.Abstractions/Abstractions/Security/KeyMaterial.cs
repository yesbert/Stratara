namespace Stratara.Abstractions.Security;

/// <summary>
/// A resolved data-encryption key: its stable id plus the raw key bytes. Returned by
/// <see cref="IKeyStore.GetOrCreateCurrentKeyAsync"/> so callers obtain both the id (to persist
/// alongside the ciphertext) and the key (to perform the encryption) in a single call.
/// </summary>
/// <param name="KeyId">The stable id of the key, persisted with the ciphertext for later resolution.</param>
/// <param name="Key">The raw key bytes. Callers should zero their copy after use.</param>
public sealed record KeyMaterial(string KeyId, ReadOnlyMemory<byte> Key);
