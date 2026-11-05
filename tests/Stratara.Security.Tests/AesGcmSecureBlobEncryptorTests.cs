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

    private static ISecureBlobEncryptor Encryptor(IKeyStore keyStore, bool legacyCarriesPurpose = false, bool writeV2 = false)
        => new AesGcmSecureBlobEncryptor(new AesGcmEncryptionFactory(), keyStore, Options.Create(new StrataraBlobEncryptionOptions
        {
            LegacyBlobsCarryPurpose = legacyCarriesPurpose,
            WriteV2Blobs = writeV2,
        }));

    private static DummyKeyStore DummyKeyStore()
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
        return new DummyKeyStore(env.Object);
    }

    [Fact]
    public async Task Encrypt_ForAnErasedTenant_SurfacesTheKeyStoresRefusal()
    {
        var keyStore = TestSupport.NewKeyStore(TestSupport.NewOptions(TestSupport.NewKekBase64()));
        await keyStore.MarkSubjectErasedAsync(KeySubject.Tenant(Tenant));

        var refused = await Assert.ThrowsAsync<KeyScopeErasedException>(() =>
            Encryptor(keyStore).EncryptAsync(new MemoryStream(Plain), Scope, "attachment"));

        Assert.Equal(Scope, refused.Scope);
    }

    [Fact]
    public async Task V2_RoundTrip_RecoversPlaintext()
    {
        var keyStore = TestSupport.NewKeyStore(TestSupport.NewOptions(TestSupport.NewKekBase64()));
        var encryptor = Encryptor(keyStore, writeV2: true);

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

    public static TheoryData<string, KeyScope, KeyScope> ForeignScopes() => new()
    {
        { "another user of the same tenant", new KeyScope(DataSensitivityLevel.UserScoped, Tenant, "alice"), new KeyScope(DataSensitivityLevel.UserScoped, Tenant, "bob") },
        { "the tenant-level scope of the same tenant", new KeyScope(DataSensitivityLevel.UserScoped, Tenant, "alice"), new KeyScope(DataSensitivityLevel.TenantScoped, Tenant) },
        { "another user without a tenant", new KeyScope(DataSensitivityLevel.UserScoped, null, "alice"), new KeyScope(DataSensitivityLevel.UserScoped, null, "bob") },
        { "another tenant", new KeyScope(DataSensitivityLevel.TenantScoped, Tenant), new KeyScope(DataSensitivityLevel.TenantScoped, "different-tenant") },
        { "another level of the same tenant", new KeyScope(DataSensitivityLevel.TenantScoped, Tenant), new KeyScope(DataSensitivityLevel.Confidential, Tenant) },
    };

    [Theory]
    [MemberData(nameof(ForeignScopes))]
    public async Task A_blob_does_not_decrypt_under_a_scope_it_was_not_written_for(string _, KeyScope written, KeyScope read)
    {
        var keyStore = TestSupport.NewKeyStore(TestSupport.NewOptions(TestSupport.NewKekBase64()));
        var encryptor = Encryptor(keyStore);

        var encrypted = ToArray(await encryptor.EncryptAsync(new MemoryStream(Plain), written, "attachment", TestContext.Current.CancellationToken));

        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
            await encryptor.DecryptAsync(new MemoryStream(encrypted), read, TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(ForeignScopes))]
    public async Task A_blob_round_trips_under_the_scope_it_was_written_for(string _, KeyScope written, KeyScope unused)
    {
        Assert.NotEqual(written, unused);
        var keyStore = TestSupport.NewKeyStore(TestSupport.NewOptions(TestSupport.NewKekBase64()));
        var encryptor = Encryptor(keyStore);

        var encrypted = ToArray(await encryptor.EncryptAsync(new MemoryStream(Plain), written, "attachment", TestContext.Current.CancellationToken));

        Assert.Equal(0x03, encrypted[0]);
        var decrypted = await encryptor.DecryptAsync(new MemoryStream(encrypted), written, TestContext.Current.CancellationToken);
        Assert.Equal(Plain, ToArray(decrypted));
    }

    [Fact]
    public async Task A_v2_blob_written_for_a_rolling_upgrade_still_starts_with_0x02_and_decrypts_in_a_v3_reader()
    {
        var keyStore = TestSupport.NewKeyStore(TestSupport.NewOptions(TestSupport.NewKekBase64()));
        var v2Writer = Encryptor(keyStore, writeV2: true);

        var encrypted = ToArray(await v2Writer.EncryptAsync(new MemoryStream(Plain), Scope, "attachment", TestContext.Current.CancellationToken));

        Assert.Equal(0x02, encrypted[0]);
        var decrypted = await Encryptor(keyStore).DecryptAsync(new MemoryStream(encrypted), Scope, TestContext.Current.CancellationToken);
        Assert.Equal(Plain, ToArray(decrypted));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_blob_does_not_decrypt_when_the_reader_expects_another_purpose(bool writeV2)
    {
        var keyStore = TestSupport.NewKeyStore(TestSupport.NewOptions(TestSupport.NewKekBase64()));
        var encryptor = Encryptor(keyStore, writeV2: writeV2);

        var encrypted = ToArray(await encryptor.EncryptAsync(new MemoryStream(Plain), Scope, "attachment", TestContext.Current.CancellationToken));

        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
            await encryptor.DecryptAsync(new MemoryStream(encrypted), Scope, "invoice", TestContext.Current.CancellationToken));
        var decrypted = await encryptor.DecryptAsync(new MemoryStream(encrypted), Scope, "attachment", TestContext.Current.CancellationToken);
        Assert.Equal(Plain, ToArray(decrypted));
    }

    [Fact]
    public async Task A_legacy_blob_without_a_purpose_field_decrypts_whatever_purpose_the_reader_expects()
    {
        var encryptor = Encryptor(DummyKeyStore(), legacyCarriesPurpose: false);
        var legacy = PackLegacy(DummyKey(), Tenant, "blob", "Development::dummy:v1", includePurpose: false);

        var decrypted = await encryptor.DecryptAsync(new MemoryStream(legacy), Scope, "attachment", TestContext.Current.CancellationToken);

        Assert.Equal(Plain, ToArray(decrypted));
    }

    [Fact]
    public async Task A_legacy_blob_with_a_purpose_field_does_not_decrypt_when_the_reader_expects_another_purpose()
    {
        var encryptor = Encryptor(DummyKeyStore(), legacyCarriesPurpose: true);
        var legacy = PackLegacy(DummyKey(), Tenant, "document", "acme-corp::v3", includePurpose: true);

        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
            await encryptor.DecryptAsync(new MemoryStream(legacy), Scope, "invoice", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x69, 0xB7, 0x1D })]
    [InlineData(new byte[] { 0x03, 0xFF, 0xFF, 0xFF, 0x7F, 0x00 })]
    [InlineData(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00, 0xAA, 0x01, 0x00, 0x00, 0x00, 0xBB, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    public async Task A_stream_that_is_not_an_encrypted_blob_fails_as_a_cryptographic_error(byte[] garbage)
    {
        var encryptor = Encryptor(TestSupport.NewKeyStore(TestSupport.NewOptions(TestSupport.NewKekBase64())));

        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
            await encryptor.DecryptAsync(new MemoryStream(garbage), Scope, "attachment", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Legacy_FormatWithoutPurpose_IsReadable()
    {
        var encryptor = Encryptor(DummyKeyStore(), legacyCarriesPurpose: false);
        var legacy = PackLegacy(DummyKey(), Tenant, "blob", "Development::dummy:v1", includePurpose: false);

        var decrypted = await encryptor.DecryptAsync(new MemoryStream(legacy), Scope);

        Assert.Equal(Plain, ToArray(decrypted));
    }

    [Fact]
    public async Task Legacy_FormatWithPurpose_IsReadable()
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
