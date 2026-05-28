using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Stratara.Abstractions.Security;

namespace Stratara.Infrastructure.Security.Cryptography;

/// <summary>
/// <see cref="ISecureBlobEncryptor"/> implementation that AES-GCM-encrypts arbitrary binary
/// streams with a per-tenant data-encryption key and packs the resulting nonce + tag + key-id +
/// ciphertext into a single self-describing stream.
/// </summary>
/// <remarks>
/// The additional-authenticated-data (AAD) binds each ciphertext to its tenant via
/// <c>tenantId||"blob"</c>. The decryption key id travels alongside the ciphertext so key rotation
/// can leave older blobs decryptable as long as the old key is still resolvable via
/// <see cref="IKeyStore"/>.
/// </remarks>
[ExcludeFromCodeCoverage]
internal sealed class SecureBlobEncryptor(
    IEncryptionFactory encryptionFactory,
    IKeyStore keyStore) : ISecureBlobEncryptor
{
    private const string BlobScope = "blob";

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Thrown when the resolved tenant key cannot be loaded.</exception>
    public async Task<Stream> EncryptAsync(
        Stream plainStream,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var plainBytes = await ReadStreamAsync(plainStream, cancellationToken);

        var keyId = await keyStore.EnsureKeyAsync(
            DataSensitivityLevel.TenantScoped, tenantId, null, cancellationToken);

        var rawKey = await keyStore.GetDataEncryptionKeyAsync(keyId, cancellationToken)
                     ?? throw new InvalidOperationException("Encryption key not available.");

        var dataEncryptionKey = (byte[])rawKey.Clone();
        try
        {
            var aad = BuildAdditionalAuthenticatedData(tenantId);
            var encrypted = encryptionFactory.Encrypt(plainBytes, dataEncryptionKey, aad);
            var keyIdBytes = Encoding.UTF8.GetBytes(keyId);

            return PackEncryptedStream(encrypted, keyIdBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataEncryptionKey);
        }
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Thrown when the embedded key id cannot be resolved to a key.</exception>
    public async Task<Stream> DecryptAsync(
        Stream encryptedStream,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var encryptedBytes = await ReadStreamAsync(encryptedStream, cancellationToken);
        var (encrypted, keyId) = UnpackEncryptedData(encryptedBytes);

        var rawKey = await keyStore.GetDataEncryptionKeyAsync(keyId, cancellationToken)
                     ?? throw new InvalidOperationException("Decryption key not available.");

        var dataEncryptionKey = (byte[])rawKey.Clone();
        try
        {
            var aad = BuildAdditionalAuthenticatedData(tenantId);
            var plainBytes = encryptionFactory.Decrypt(encrypted, dataEncryptionKey, aad);

            return new MemoryStream(plainBytes, writable: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataEncryptionKey);
        }
    }

    private static MemoryStream PackEncryptedStream(EncryptedData data, byte[] keyIdBytes)
    {
        var totalLength = 4 + data.Nonce.Length + 4 + data.Tag.Length + 4 + keyIdBytes.Length + data.CipherText.Length;
        var buffer = new byte[totalLength];
        var offset = 0;

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), data.Nonce.Length);
        offset += 4;
        data.Nonce.CopyTo(buffer, offset);
        offset += data.Nonce.Length;

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), data.Tag.Length);
        offset += 4;
        data.Tag.CopyTo(buffer, offset);
        offset += data.Tag.Length;

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), keyIdBytes.Length);
        offset += 4;
        keyIdBytes.CopyTo(buffer, offset);
        offset += keyIdBytes.Length;

        data.CipherText.CopyTo(buffer, offset);

        return new MemoryStream(buffer, writable: false);
    }

    private static (EncryptedData Data, string KeyId) UnpackEncryptedData(byte[] bytes)
    {
        var offset = 0;

        var nonceLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        offset += 4;
        var nonce = bytes.AsSpan(offset, nonceLength).ToArray();
        offset += nonceLength;

        var tagLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        offset += 4;
        var tag = bytes.AsSpan(offset, tagLength).ToArray();
        offset += tagLength;

        var keyIdLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        offset += 4;
        var keyId = Encoding.UTF8.GetString(bytes.AsSpan(offset, keyIdLength));
        offset += keyIdLength;

        var cipherText = bytes.AsSpan(offset).ToArray();

        var data = new EncryptedData { Nonce = nonce, Tag = tag, CipherText = cipherText };
        return (data, keyId);
    }

    private static byte[] BuildAdditionalAuthenticatedData(Guid tenantId)
        => Encoding.UTF8.GetBytes($"{tenantId}||{BlobScope}");

    private static async Task<byte[]> ReadStreamAsync(Stream stream, CancellationToken cancellationToken)
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
