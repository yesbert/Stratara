namespace Stratara.Abstractions.Security;

/// <summary>
/// Streaming AES-GCM wrapper for blob payloads. Encryption + decryption both bind the
/// stream to a tenant id via associated data so a leaked ciphertext can't be decrypted
/// against another tenant's key.
/// </summary>
public interface ISecureBlobEncryptor
{
    /// <summary>Encrypt <paramref name="plainStream"/> for <paramref name="tenantId"/>.</summary>
    Task<Stream> EncryptAsync(Stream plainStream, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Decrypt <paramref name="encryptedStream"/> for <paramref name="tenantId"/>. AAD mismatch throws.</summary>
    Task<Stream> DecryptAsync(Stream encryptedStream, Guid tenantId, CancellationToken cancellationToken = default);
}
