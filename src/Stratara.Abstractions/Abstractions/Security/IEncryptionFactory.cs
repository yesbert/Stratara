using Stratara.Abstractions.Security;

namespace Stratara.Abstractions.Security;

/// <summary>
/// Symmetric authenticated-encryption primitive (AES-GCM in the default impl). The
/// associated-data parameter binds ciphertext to a context — typically the Subject
/// tenant id / user id — to prevent ciphertext swapping across tenants.
/// </summary>
public interface IEncryptionFactory
{
    /// <summary>Encrypt <paramref name="plaintext"/> using <paramref name="key"/> bound to <paramref name="associatedData"/>.</summary>
    EncryptedData Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> associatedData);

    /// <summary>Decrypt <paramref name="data"/> using <paramref name="key"/>; the associated data must match what was used at encryption time.</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">Authentication tag verification failed (wrong key or AAD).</exception>
    byte[] Decrypt(EncryptedData data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> additionalAuthenticatedData);
}
