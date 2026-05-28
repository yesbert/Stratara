using System.Security.Cryptography;
using System.Text;

namespace Stratara.Sample.Encryption.Crypto;

// AES-256-GCM with an AAD bound to the tenant id. Same master key for every
// tenant — the cross-tenant separation comes from the AAD binding, not from
// per-tenant key isolation. (Real Stratara additionally per-tenant-derives keys
// via HKDF on top of an IKeyStore; this sample keeps it to one moving part so
// the AAD trick is visible.)
public sealed class TenantAwareEncryptor
{
    private readonly byte[] _masterKey;

    public TenantAwareEncryptor(byte[] masterKey)
    {
        if (masterKey.Length != 32)
        {
            throw new ArgumentException("Master key must be 32 bytes for AES-256.", nameof(masterKey));
        }
        _masterKey = masterKey;
    }

    public SealedField Encrypt(string plaintext, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[16];
        var aad = tenantId.ToByteArray();

        using var aes = new AesGcm(_masterKey, tagSizeInBytes: 16);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, aad);

        return new SealedField(nonce, ciphertext, tag);
    }

    public string Decrypt(SealedField sealedField, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(sealedField);

        var plaintext = new byte[sealedField.Ciphertext.Length];
        var aad = tenantId.ToByteArray();

        using var aes = new AesGcm(_masterKey, tagSizeInBytes: 16);
        // Throws CryptographicException if the AAD (tenant id) doesn't match
        // the one the field was sealed under — even with the correct key.
        aes.Decrypt(sealedField.Nonce, sealedField.Ciphertext, sealedField.Tag, plaintext, aad);
        return Encoding.UTF8.GetString(plaintext);
    }
}
