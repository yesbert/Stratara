using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Security;

namespace Stratara.Security;

/// <summary>
/// <see cref="ISecureBlobEncryptor"/> that AES-GCM-encrypts streams under a scope's current DEK and
/// packs nonce + tag + key id + purpose + ciphertext into a single self-describing stream with a
/// leading version byte (v2).
/// </summary>
/// <remarks>
/// <para>
/// The associated data is <c>{tenant}||{purpose}</c>, binding each ciphertext to its tenant scope
/// and purpose. The embedded key id lets rotation keep older blobs decryptable while the old key is
/// still resolvable via <see cref="IKeyStore"/>.
/// </para>
/// <para>
/// <strong>Format.</strong> v2 streams start with the byte <c>0x02</c> followed by four
/// little-endian length-prefixed fields (nonce, tag, key id, purpose) and the ciphertext. Streams
/// without the leading <c>0x02</c> are read as legacy: a valid legacy stream begins with a 12-byte
/// nonce length (<c>0x0C,0x00,0x00,0x00</c>), so the first byte is never <c>0x02</c>. Whether a
/// legacy stream carries a purpose field is controlled by
/// <see cref="StrataraBlobEncryptionOptions.LegacyBlobsCarryPurpose"/>.
/// </para>
/// </remarks>
internal sealed class AesGcmSecureBlobEncryptor(
    IEncryptionFactory encryptionFactory,
    IKeyStore keyStore,
    IOptions<StrataraBlobEncryptionOptions> options) : ISecureBlobEncryptor
{
    private const byte V2VersionByte = 0x02;
    private const string DefaultLegacyPurpose = "blob";

    /// <inheritdoc/>
    public async Task<Stream> EncryptAsync(Stream plainStream, KeyScope scope, string purpose, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        var plainBytes = await ReadAllAsync(plainStream, cancellationToken);

        var keyMaterial = await keyStore.GetOrCreateCurrentKeyAsync(scope, cancellationToken);
        var dataEncryptionKey = keyMaterial.Key.ToArray();
        try
        {
            var aad = BuildAad(scope, purpose);
            var encrypted = encryptionFactory.Encrypt(plainBytes, dataEncryptionKey, aad);
            return Pack(encrypted, keyMaterial.KeyId, purpose);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataEncryptionKey);
        }
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The embedded key id cannot be resolved (revoked / erased / unknown).</exception>
    public async Task<Stream> DecryptAsync(Stream encryptedStream, KeyScope scope, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadAllAsync(encryptedStream, cancellationToken);
        var (encrypted, keyId, purpose) = Unpack(bytes, options.Value.LegacyBlobsCarryPurpose);

        var dataEncryptionKey = await keyStore.GetDataEncryptionKeyAsync(keyId, cancellationToken)
                                ?? throw new InvalidOperationException($"Decryption key '{keyId}' is not available (revoked, erased, or unknown).");
        try
        {
            var aad = BuildAad(scope, purpose);
            var plainBytes = encryptionFactory.Decrypt(encrypted, dataEncryptionKey, aad);
            return new MemoryStream(plainBytes, writable: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataEncryptionKey);
        }
    }

    private static MemoryStream Pack(EncryptedData data, string keyId, string purpose)
    {
        var keyIdBytes = Encoding.UTF8.GetBytes(keyId);
        var purposeBytes = Encoding.UTF8.GetBytes(purpose);

        var totalLength = 1
            + 4 + data.Nonce.Length
            + 4 + data.Tag.Length
            + 4 + keyIdBytes.Length
            + 4 + purposeBytes.Length
            + data.CipherText.Length;

        var buffer = new byte[totalLength];
        var offset = 0;
        buffer[offset++] = V2VersionByte;
        offset = WriteField(buffer, offset, data.Nonce);
        offset = WriteField(buffer, offset, data.Tag);
        offset = WriteField(buffer, offset, keyIdBytes);
        offset = WriteField(buffer, offset, purposeBytes);
        data.CipherText.CopyTo(buffer, offset);

        return new MemoryStream(buffer, writable: false);
    }

    private static (EncryptedData Data, string KeyId, string Purpose) Unpack(byte[] bytes, bool legacyCarriesPurpose)
    {
        var offset = 0;
        var isV2 = bytes.Length > 0 && bytes[0] == V2VersionByte;
        if (isV2)
        {
            offset = 1;
        }

        var (nonce, afterNonce) = ReadField(bytes, offset);
        var (tag, afterTag) = ReadField(bytes, afterNonce);
        var (keyIdBytes, afterKeyId) = ReadField(bytes, afterTag);

        string purpose;
        int afterPurpose;
        if (isV2 || legacyCarriesPurpose)
        {
            var (purposeBytes, next) = ReadField(bytes, afterKeyId);
            purpose = Encoding.UTF8.GetString(purposeBytes);
            afterPurpose = next;
        }
        else
        {
            purpose = DefaultLegacyPurpose;
            afterPurpose = afterKeyId;
        }

        var cipherText = bytes.AsSpan(afterPurpose).ToArray();
        var data = new EncryptedData { Nonce = nonce, Tag = tag, CipherText = cipherText };
        return (data, Encoding.UTF8.GetString(keyIdBytes), purpose);
    }

    private static int WriteField(byte[] buffer, int offset, byte[] field)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), field.Length);
        offset += 4;
        field.CopyTo(buffer, offset);
        return offset + field.Length;
    }

    private static (byte[] Field, int NextOffset) ReadField(byte[] bytes, int offset)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        offset += 4;
        var field = bytes.AsSpan(offset, length).ToArray();
        return (field, offset + length);
    }

    private static byte[] BuildAad(KeyScope scope, string purpose) => Encoding.UTF8.GetBytes($"{scope.TenantId}||{purpose}");

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream is MemoryStream memoryStream && memoryStream.TryGetBuffer(out var segment))
        {
            return segment.ToArray();
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
