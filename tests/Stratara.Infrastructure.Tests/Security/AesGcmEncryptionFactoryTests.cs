using System.Security.Cryptography;
using Stratara.Infrastructure.Security.Cryptography;
using Stratara.Abstractions.Security;

namespace Stratara.Infrastructure.Tests.Security;

public class AesGcmEncryptionFactoryTests
{
    private readonly AesGcmEncryptionFactory _factory = new();

    private static byte[] GenerateKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    [Fact]
    public void Encrypt_ReturnsEncryptedData_WithCorrectSizes()
    {
        var key = GenerateKey();
        var plaintext = "Hello, World!"u8.ToArray();
        var aad = "tenant|user|scope"u8.ToArray();

        var result = _factory.Encrypt(plaintext, key, aad);

        Assert.Equal(12, result.Nonce.Length);
        Assert.Equal(16, result.Tag.Length);
        Assert.Equal(plaintext.Length, result.CipherText.Length);
    }

    [Fact]
    public void Encrypt_Decrypt_RoundTrip_ReturnsOriginal()
    {
        var key = GenerateKey();
        var plaintext = "Secret data to encrypt"u8.ToArray();
        var aad = "tenant|user|scope"u8.ToArray();

        var encrypted = _factory.Encrypt(plaintext, key, aad);
        var decrypted = _factory.Decrypt(encrypted, key, aad);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_DifferentNonces_EachTime()
    {
        var key = GenerateKey();
        var plaintext = "Same data"u8.ToArray();
        var aad = "tenant|user|scope"u8.ToArray();

        var result1 = _factory.Encrypt(plaintext, key, aad);
        var result2 = _factory.Encrypt(plaintext, key, aad);

        Assert.NotEqual(result1.Nonce, result2.Nonce);
    }

    [Fact]
    public void Decrypt_WrongKey_ThrowsCryptographicException()
    {
        var key = GenerateKey();
        var wrongKey = GenerateKey();
        var plaintext = "Sensitive data"u8.ToArray();
        var aad = "tenant|user|scope"u8.ToArray();

        var encrypted = _factory.Encrypt(plaintext, key, aad);

        Assert.ThrowsAny<CryptographicException>(() =>
            _factory.Decrypt(encrypted, wrongKey, aad));
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_ThrowsCryptographicException()
    {
        var key = GenerateKey();
        var plaintext = "Original data"u8.ToArray();
        var aad = "tenant|user|scope"u8.ToArray();

        var encrypted = _factory.Encrypt(plaintext, key, aad);
        encrypted.CipherText[0] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() =>
            _factory.Decrypt(encrypted, key, aad));
    }

    [Fact]
    public void Decrypt_TamperedTag_ThrowsCryptographicException()
    {
        var key = GenerateKey();
        var plaintext = "Original data"u8.ToArray();
        var aad = "tenant|user|scope"u8.ToArray();

        var encrypted = _factory.Encrypt(plaintext, key, aad);
        encrypted.Tag[0] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() =>
            _factory.Decrypt(encrypted, key, aad));
    }

    [Fact]
    public void Encrypt_WithAssociatedData_Decrypt_RequiresSameAAD()
    {
        var key = GenerateKey();
        var plaintext = "Protected data"u8.ToArray();
        var aad = "tenant|user|scope"u8.ToArray();
        var wrongAad = "wrong|data|here"u8.ToArray();

        var encrypted = _factory.Encrypt(plaintext, key, aad);

        Assert.ThrowsAny<CryptographicException>(() =>
            _factory.Decrypt(encrypted, key, wrongAad));
    }

    [Fact]
    public void Encrypt_EmptyPlaintext_Works()
    {
        var key = GenerateKey();
        var plaintext = Array.Empty<byte>();
        var aad = "tenant|user|scope"u8.ToArray();

        var encrypted = _factory.Encrypt(plaintext, key, aad);
        var decrypted = _factory.Decrypt(encrypted, key, aad);

        Assert.Empty(decrypted);
    }
}
