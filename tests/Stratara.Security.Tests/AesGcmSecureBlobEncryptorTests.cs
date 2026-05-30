using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.Security;
using Xunit;

namespace Stratara.Security.Tests;

public class AesGcmSecureBlobEncryptorTests
{
    private const string Tenant = "acme-corp";
    private static readonly KeyScope Scope = new(DataSensitivityLevel.TenantScoped, Tenant);
    private static readonly byte[] Plain = "the quick brown fox jumps over the lazy dog"u8.ToArray();

    // The deterministic key DummyKeyStore derives from its default pass-phrase.
    private static byte[] DummyKey() => SHA256.HashData(Encoding.UTF8.GetBytes("StrataraTestKey"));

    private static AesGcmSecureBlobEncryptor Encryptor(IKeyStore keyStore, bool legacyCarriesPurpose = false)
        => new(new AesGcmEncryptionFactory(), keyStore, Options.Create(new StrataraBlobEncryptionOptions { LegacyBlobsCarryPurpose = legacyCarriesPurpose }));

    private static DummyKeyStore DummyKeyStore()
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
        return new DummyKeyStore(env.Object);
    }

    [Fact]
    public async Task V2_RoundTrip_RecoversPlaintext()
    {
        var keyStore = TestSupport.NewKeyStore(TestSupport.NewOptions(TestSupport.NewKekBase64()));
        var encryptor = Encryptor(keyStore);

        var encryptedStream = await encryptor.EncryptAsync(new MemoryStream(Plain), Scope, "attachment");
        var encrypted = ToArray(encryptedStream);

        Assert.Equal(0x02, encrypted[0]); // v2 version byte

        var decryptedStream = await encryptor.DecryptAsync(new MemoryStream(encrypted), Scope);
        Assert.Equal(Plain, ToArray(decryptedStream));
    }

    [Fact]
    public async Task V2_DecryptingUnderDifferentScope_FailsAadCheck()
    {
        var keyStore = TestSupport.NewKeyStore(TestSupport.NewOptions(TestSupport.NewKekBase64()));
        var encryptor = Encryptor(keyStore);

        var encrypted = ToArray(await encryptor.EncryptAsync(new MemoryStream(Plain), Scope, "attachment"));

        // The key id still resolves, but the tenant in the AAD differs, so GCM authentication fails.
        var otherScope = new KeyScope(DataSensitivityLevel.TenantScoped, "different-tenant");
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await encryptor.DecryptAsync(new MemoryStream(encrypted), otherScope));
    }

    [Fact]
    public async Task Legacy_NextPaFormat_NoPurpose_IsReadable()
    {
        var encryptor = Encryptor(DummyKeyStore(), legacyCarriesPurpose: false);
        var legacy = PackLegacy(DummyKey(), Tenant, "blob", "Development::dummy:v1", includePurpose: false);

        var decrypted = await encryptor.DecryptAsync(new MemoryStream(legacy), Scope);

        Assert.Equal(Plain, ToArray(decrypted));
    }

    [Fact]
    public async Task Legacy_VeloxRagFormat_WithPurpose_IsReadable()
    {
        var encryptor = Encryptor(DummyKeyStore(), legacyCarriesPurpose: true);
        var legacy = PackLegacy(DummyKey(), Tenant, "document", "acme-corp::v3", includePurpose: true);

        var decrypted = await encryptor.DecryptAsync(new MemoryStream(legacy), Scope);

        Assert.Equal(Plain, ToArray(decrypted));
    }

    private static byte[] PackLegacy(byte[] key, string tenant, string purpose, string keyId, bool includePurpose)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[Plain.Length];
        var aad = Encoding.UTF8.GetBytes($"{tenant}||{purpose}");
        using (var gcm = new AesGcm(key, 16))
        {
            gcm.Encrypt(nonce, Plain, ciphertext, tag, aad);
        }

        using var ms = new MemoryStream();
        WriteField(ms, nonce);
        WriteField(ms, tag);
        WriteField(ms, Encoding.UTF8.GetBytes(keyId));
        if (includePurpose)
        {
            WriteField(ms, Encoding.UTF8.GetBytes(purpose));
        }

        ms.Write(ciphertext);
        return ms.ToArray();
    }

    private static void WriteField(Stream stream, byte[] field)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, field.Length);
        stream.Write(len);
        stream.Write(field);
    }

    private static byte[] ToArray(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
