namespace Stratara.Abstractions.Security;

/// <summary>
/// Streaming AES-GCM wrapper for blob payloads. Encryption + decryption bind the stream to a
/// <see cref="KeyScope"/> and a <c>purpose</c> via associated data, so a leaked ciphertext cannot
/// be decrypted against another scope's key or reused under a different purpose.
/// </summary>
public interface ISecureBlobEncryptor
{
    /// <summary>
    /// Encrypt <paramref name="plainStream"/> under the current key for <paramref name="scope"/>,
    /// binding the ciphertext to <paramref name="purpose"/>.
    /// </summary>
    /// <param name="plainStream">The plaintext to encrypt.</param>
    /// <param name="scope">The key scope whose current key encrypts the payload.</param>
    /// <param name="purpose">A caller-defined label folded into the associated data (e.g. <c>"blob"</c>, <c>"attachment"</c>).</param>
    /// <param name="cancellationToken">Propagated to the key store and stream operations.</param>
    /// <returns>A self-describing encrypted stream (version byte + nonce + tag + key id + purpose + ciphertext).</returns>
    Task<Stream> EncryptAsync(Stream plainStream, KeyScope scope, string purpose, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrypt <paramref name="encryptedStream"/> for <paramref name="scope"/>. The embedded key id
    /// and purpose are read from the stream; an associated-data mismatch throws.
    /// </summary>
    /// <param name="encryptedStream">The encrypted stream produced by <see cref="EncryptAsync"/> (or a supported legacy format).</param>
    /// <param name="scope">The key scope the ciphertext was bound to.</param>
    /// <param name="cancellationToken">Propagated to the key store and stream operations.</param>
    /// <returns>The decrypted plaintext stream.</returns>
    Task<Stream> DecryptAsync(Stream encryptedStream, KeyScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrypt <paramref name="encryptedStream"/> for <paramref name="scope"/>, requiring that it was
    /// written for <paramref name="purpose"/>. A stream written for another purpose fails
    /// authentication instead of decrypting.
    /// </summary>
    /// <remarks>
    /// The default implementation delegates to
    /// <see cref="DecryptAsync(Stream, KeyScope, CancellationToken)"/> and does not check the
    /// purpose; an implementation overrides it to enforce the purpose. A stream in a format that
    /// records no purpose cannot be checked against one and decrypts as before.
    /// </remarks>
    /// <param name="encryptedStream">The encrypted stream produced by <see cref="EncryptAsync"/> (or a supported legacy format).</param>
    /// <param name="scope">The key scope the ciphertext was bound to.</param>
    /// <param name="purpose">The purpose the ciphertext is expected to have been written for.</param>
    /// <param name="cancellationToken">Propagated to the key store and stream operations.</param>
    /// <returns>The decrypted plaintext stream.</returns>
    Task<Stream> DecryptAsync(Stream encryptedStream, KeyScope scope, string purpose, CancellationToken cancellationToken = default) =>
        DecryptAsync(encryptedStream, scope, cancellationToken);
}
