using System.Security.Cryptography;
using Stratara.Abstractions.Security;

namespace Stratara.Security;

/// <summary>
/// <see cref="IEncryptionFactory"/> implementation backed by AES-GCM (Galois/Counter Mode) with
/// 96-bit nonces and 128-bit authentication tags.
/// </summary>
/// <remarks>
/// <para>
/// AES-GCM is an AEAD (authenticated-encryption with associated data) cipher. The
/// <c>associatedData</c> parameter is authenticated but NOT encrypted; Stratara uses it to bind
/// ciphertexts to a scope so a ciphertext copied from one context to another fails decryption.
/// </para>
/// <para>
/// <strong>Nonce uniqueness:</strong> a nonce MUST never repeat under the same key. This factory
/// fills the nonce from <see cref="RandomNumberGenerator"/>. Rotate the key before the per-key
/// encryption count approaches the GCM birthday bound (~2^32).
/// </para>
/// </remarks>
internal sealed class AesGcmEncryptionFactory : IEncryptionFactory
{
    /// <inheritdoc/>
    public EncryptedData Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> associatedData)
    {
        var nonce = GC.AllocateUninitializedArray<byte>(CryptoConstants.NonceSize);
        var tag = GC.AllocateUninitializedArray<byte>(CryptoConstants.TagSize);
        var ciphertext = GC.AllocateUninitializedArray<byte>(plaintext.Length);

        RandomNumberGenerator.Fill(nonce);

        using var gcm = new AesGcm(key, CryptoConstants.TagSize);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        return new EncryptedData { CipherText = ciphertext, Nonce = nonce, Tag = tag };
    }

    /// <inheritdoc/>
    /// <exception cref="CryptographicException">
    /// Thrown by AES-GCM when the tag verification fails — either the ciphertext or the
    /// <paramref name="additionalAuthenticatedData"/> has been tampered with.
    /// </exception>
    public byte[] Decrypt(EncryptedData data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> additionalAuthenticatedData)
    {
        var plaintext = GC.AllocateUninitializedArray<byte>(data.CipherText.Length);

        using var gcm = new AesGcm(key, CryptoConstants.TagSize);
        gcm.Decrypt(data.Nonce, data.CipherText, data.Tag, plaintext, additionalAuthenticatedData);

        return plaintext;
    }
}
