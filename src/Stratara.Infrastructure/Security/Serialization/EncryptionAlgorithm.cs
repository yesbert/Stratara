namespace Stratara.Infrastructure.Security.Serialization;

/// <summary>Identifies the encryption algorithm embedded in a persisted encrypted JSON envelope.</summary>
/// <remarks>
/// Stored as the <c>alg</c> field on the encryption wrapper. New values may be appended, but
/// existing values MUST NEVER be removed or repurposed — older ciphertexts depend on the
/// numeric identity for decryption-time algorithm selection.
/// </remarks>
public enum EncryptionAlgorithm
{
    /// <summary>AES-256 in Galois/Counter Mode (AEAD) with 96-bit nonces and 128-bit tags.</summary>
    Aes256Gcm = 1
}
