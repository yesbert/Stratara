using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Security;

namespace Stratara.Security;

/// <summary>
/// <see cref="ISecureBlobEncryptor"/> that AES-GCM-encrypts streams under a scope's current DEK and
/// packs nonce + tag + key id + purpose + ciphertext into a single self-describing stream with a
/// leading version byte (v3, or v2 when <see cref="StrataraBlobEncryptionOptions.WriteV2Blobs"/> is set).
/// </summary>
/// <remarks>
/// <para>
/// The associated data of a v3 stream covers the whole scope — level, tenant and user — and the
/// purpose, each field length-prefixed so none can be shifted into another. A v2 or legacy stream
/// keeps its original associated data, <c>{tenant}||{purpose}</c>. The embedded key id lets
/// rotation keep older blobs decryptable while the old key is still resolvable via <see cref="IKeyStore"/>.
/// </para>
/// <para>
/// <strong>Format.</strong> v2 and v3 streams start with the byte <c>0x02</c> or <c>0x03</c>
/// followed by four little-endian length-prefixed fields (nonce, tag, key id, purpose) and the
/// ciphertext. Streams without a leading version byte are read as legacy: a valid legacy stream
/// begins with a 12-byte nonce length (<c>0x0C,0x00,0x00,0x00</c>), so the first byte is never
/// <c>0x02</c> or <c>0x03</c>. Whether a legacy stream carries a purpose field is controlled by
/// <see cref="StrataraBlobEncryptionOptions.LegacyBlobsCarryPurpose"/>.
/// </para>
/// </remarks>
internal sealed class AesGcmSecureBlobEncryptor(
    IEncryptionFactory encryptionFactory,
    IKeyStore keyStore,
    IOptions<StrataraBlobEncryptionOptions> options) : ISecureBlobEncryptor
{
    private const byte V2VersionByte = 0x02;
    private const byte V3VersionByte = 0x03;
    private const byte LegacyFormat = 0x00;
    private const int AbsentFieldLength = -1;
    private const string DefaultLegacyPurpose = "blob";

    /// <inheritdoc/>
    public async Task<Stream> EncryptAsync(Stream plainStream, KeyScope scope, string purpose, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        var plainBytes = await ReadAllAsync(plainStream, cancellationToken);
        var version = options.Value.WriteV2Blobs ? V2VersionByte : V3VersionByte;

        var keyMaterial = await keyStore.GetOrCreateCurrentKeyAsync(scope, cancellationToken);
        var dataEncryptionKey = keyMaterial.Key.ToArray();
        try
        {
            var aad = BuildAad(version, scope, purpose);
            var encrypted = encryptionFactory.Encrypt(plainBytes, dataEncryptionKey, aad);
            return Pack(version, encrypted, keyMaterial.KeyId, purpose);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataEncryptionKey);
        }
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The embedded key id cannot be resolved (revoked / erased / unknown).</exception>
    /// <exception cref="CryptographicException">The stream is malformed, or it fails authentication under the scope and purpose.</exception>
    public Task<Stream> DecryptAsync(Stream encryptedStream, KeyScope scope, CancellationToken cancellationToken = default) =>
        DecryptCoreAsync(encryptedStream, scope, expectedPurpose: null, cancellationToken);

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The embedded key id cannot be resolved (revoked / erased / unknown).</exception>
    /// <exception cref="CryptographicException">The stream is malformed, or it fails authentication under the scope and purpose.</exception>
    public Task<Stream> DecryptAsync(Stream encryptedStream, KeyScope scope, string purpose, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        return DecryptCoreAsync(encryptedStream, scope, purpose, cancellationToken);
    }

    private async Task<Stream> DecryptCoreAsync(Stream encryptedStream, KeyScope scope, string? expectedPurpose, CancellationToken cancellationToken)
    {
        var bytes = await ReadAllAsync(encryptedStream, cancellationToken);
        var blob = Unpack(bytes, options.Value.LegacyBlobsCarryPurpose);
        var purpose = expectedPurpose is not null && blob.CarriesPurpose ? expectedPurpose : blob.Purpose;

        var dataEncryptionKey = await keyStore.GetDataEncryptionKeyAsync(blob.KeyId, cancellationToken)
                                ?? throw new InvalidOperationException($"Decryption key '{blob.KeyId}' is not available (revoked, erased, or unknown).");
        try
        {
            var aad = BuildAad(blob.Version, scope, purpose);
            var plainBytes = encryptionFactory.Decrypt(blob.Data, dataEncryptionKey, aad);
            return new MemoryStream(plainBytes, writable: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataEncryptionKey);
        }
    }

    private static MemoryStream Pack(byte version, EncryptedData data, string keyId, string purpose)
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
        buffer[offset++] = version;
        offset = WriteField(buffer, offset, data.Nonce);
        offset = WriteField(buffer, offset, data.Tag);
        offset = WriteField(buffer, offset, keyIdBytes);
        offset = WriteField(buffer, offset, purposeBytes);
        data.CipherText.CopyTo(buffer, offset);

        return new MemoryStream(buffer, writable: false);
    }

    private static UnpackedBlob Unpack(byte[] bytes, bool legacyCarriesPurpose)
    {
        var version = bytes.Length > 0 && bytes[0] is V2VersionByte or V3VersionByte ? bytes[0] : LegacyFormat;
        var offset = version == LegacyFormat ? 0 : 1;

        var (nonce, afterNonce) = ReadField(bytes, offset);
        var (tag, afterTag) = ReadField(bytes, afterNonce);
        var (keyIdBytes, afterKeyId) = ReadField(bytes, afterTag);

        var carriesPurpose = version != LegacyFormat || legacyCarriesPurpose;
        string purpose;
        int afterPurpose;
        if (carriesPurpose)
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

        if (nonce.Length != CryptoConstants.NonceSize || tag.Length != CryptoConstants.TagSize)
        {
            throw MalformedStream();
        }

        var cipherText = bytes.AsSpan(afterPurpose).ToArray();
        var data = new EncryptedData { Nonce = nonce, Tag = tag, CipherText = cipherText };
        return new UnpackedBlob(version, data, Encoding.UTF8.GetString(keyIdBytes), purpose, carriesPurpose);
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
        if (bytes.Length - offset < sizeof(int))
        {
            throw MalformedStream();
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        offset += 4;
        if (length < 0 || length > bytes.Length - offset)
        {
            throw MalformedStream();
        }

        var field = bytes.AsSpan(offset, length).ToArray();
        return (field, offset + length);
    }

    private static CryptographicException MalformedStream() => new("The stream is not an encrypted blob in a format this encryptor reads.");

    private static byte[] BuildAad(byte version, KeyScope scope, string purpose) =>
        version == V3VersionByte ? BuildScopeAad(scope, purpose) : Encoding.UTF8.GetBytes($"{scope.TenantId}||{purpose}");

    private static byte[] BuildScopeAad(KeyScope scope, string purpose)
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte(V3VersionByte);
        WriteInt32(buffer, (int)scope.Level);
        WriteOptionalString(buffer, scope.TenantId);
        WriteOptionalString(buffer, scope.UserId);
        WriteOptionalString(buffer, purpose);
        return buffer.ToArray();
    }

    private static void WriteOptionalString(MemoryStream buffer, string? value)
    {
        if (value is null)
        {
            WriteInt32(buffer, AbsentFieldLength);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(buffer, bytes.Length);
        buffer.Write(bytes);
    }

    private static void WriteInt32(MemoryStream buffer, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        buffer.Write(bytes);
    }

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

    private sealed record UnpackedBlob(byte Version, EncryptedData Data, string KeyId, string Purpose, bool CarriesPurpose);
}
