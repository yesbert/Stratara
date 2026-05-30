using System.Security.Cryptography;
using Stratara.Abstractions.Security;
using Xunit;

namespace Stratara.Security.Tests;

public class EnvelopeFileKeyStoreTests
{
    private static readonly KeyScope Scope = new(DataSensitivityLevel.TenantScoped, "acme-corp");

    [Fact]
    public async Task GetOrCreateCurrent_CreatesV1_With32ByteKey()
    {
        var store = TestSupport.NewKeyStore(TestSupport.NewOptions(TestSupport.NewKekBase64()));

        var material = await store.GetOrCreateCurrentKeyAsync(Scope);

        Assert.EndsWith(":v1", material.KeyId);
        Assert.Equal(32, material.Key.Length);
    }

    [Fact]
    public async Task GetOrCreateCurrent_IsStableAcrossCalls()
    {
        var store = TestSupport.NewKeyStore(TestSupport.NewOptions(TestSupport.NewKekBase64()));

        var first = await store.GetOrCreateCurrentKeyAsync(Scope);
        var second = await store.GetOrCreateCurrentKeyAsync(Scope);

        Assert.Equal(first.KeyId, second.KeyId);
        Assert.Equal(first.Key.ToArray(), second.Key.ToArray());
    }

    [Fact]
    public async Task Rotate_CreatesNewCurrent_OldKeyStillResolvable()
    {
        var store = TestSupport.NewKeyStore(TestSupport.NewOptions(TestSupport.NewKekBase64()));

        var v1 = await store.GetOrCreateCurrentKeyAsync(Scope);
        var v2KeyId = await store.RotateAsync(Scope);
        var current = await store.GetOrCreateCurrentKeyAsync(Scope);

        Assert.NotEqual(v1.KeyId, v2KeyId);
        Assert.EndsWith(":v2", v2KeyId);
        Assert.Equal(v2KeyId, current.KeyId);

        // The old version is still decryptable so existing ciphertext survives rotation.
        var oldKey = await store.GetDataEncryptionKeyAsync(v1.KeyId);
        Assert.NotNull(oldKey);
        Assert.Equal(v1.Key.ToArray(), oldKey);
    }

    [Fact]
    public async Task Revoke_MakesKeyUnresolvable_AndCurrentRollsToNewVersion()
    {
        var store = TestSupport.NewKeyStore(TestSupport.NewOptions(TestSupport.NewKekBase64()));

        var v1 = await store.GetOrCreateCurrentKeyAsync(Scope);
        await store.RevokeAsync(v1.KeyId);

        Assert.Null(await store.GetDataEncryptionKeyAsync(v1.KeyId));

        // The next current must be a fresh, non-revoked version.
        var current = await store.GetOrCreateCurrentKeyAsync(Scope);
        Assert.NotEqual(v1.KeyId, current.KeyId);
        Assert.NotNull(await store.GetDataEncryptionKeyAsync(current.KeyId));
    }

    [Fact]
    public async Task EraseScope_MakesAllVersionsUnrecoverable()
    {
        var options = TestSupport.NewOptions(TestSupport.NewKekBase64());
        var store = TestSupport.NewKeyStore(options);

        var v1 = await store.GetOrCreateCurrentKeyAsync(Scope);
        var v2 = await store.RotateAsync(Scope);

        await store.EraseScopeAsync(Scope);

        Assert.Null(await store.GetDataEncryptionKeyAsync(v1.KeyId));
        Assert.Null(await store.GetDataEncryptionKeyAsync(v2));

        // A fresh keystore reading the same file must not recover the erased material either.
        var reopened = TestSupport.NewKeyStore(options);
        Assert.Null(await reopened.GetDataEncryptionKeyAsync(v1.KeyId));
    }

    [Fact]
    public async Task StoreFile_ContainsOnlyWrappedKeys_NeverPlaintext()
    {
        var options = TestSupport.NewOptions(TestSupport.NewKekBase64());
        var store = TestSupport.NewKeyStore(options);

        var material = await store.GetOrCreateCurrentKeyAsync(Scope);
        var fileContents = await File.ReadAllTextAsync(options.StorePath);

        // The raw DEK must never appear in the persisted file — only its KEK-wrapped form.
        var plaintextBase64 = Convert.ToBase64String(material.Key.ToArray());
        Assert.DoesNotContain(plaintextBase64, fileContents);
    }

    [Fact]
    public async Task WrongMasterKey_CannotUnwrap()
    {
        var options = TestSupport.NewOptions(TestSupport.NewKekBase64());
        var store = TestSupport.NewKeyStore(options);
        var v1 = await store.GetOrCreateCurrentKeyAsync(Scope);

        // Re-open the same store file with a different KEK.
        var tamperedOptions = new StrataraFileKeyStoreOptions
        {
            MasterKeyBase64 = TestSupport.NewKekBase64(),
            StorePath = options.StorePath,
        };
        var wrongKekStore = TestSupport.NewKeyStore(tamperedOptions);

        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await wrongKekStore.GetDataEncryptionKeyAsync(v1.KeyId));
    }

    [Fact]
    public async Task DifferentScopes_GetDistinctKeys()
    {
        var store = TestSupport.NewKeyStore(TestSupport.NewOptions(TestSupport.NewKekBase64()));

        var tenantA = await store.GetOrCreateCurrentKeyAsync(new KeyScope(DataSensitivityLevel.TenantScoped, "tenant-a"));
        var tenantB = await store.GetOrCreateCurrentKeyAsync(new KeyScope(DataSensitivityLevel.TenantScoped, "tenant-b"));

        Assert.NotEqual(tenantA.KeyId, tenantB.KeyId);
        Assert.NotEqual(tenantA.Key.ToArray(), tenantB.Key.ToArray());
    }
}
