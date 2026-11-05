using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Settings;
using Stratara.Diagnostics;
using Stratara.Testing;
using Xunit;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

public class EncryptingSettingStoreTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();

    private static SettingCatalog Catalog()
    {
        var catalog = new SettingCatalog();
        catalog.Add(
            new SettingDefinition("Smtp.Password", IsEncrypted: true),
            new SettingDefinition("Api.Token", IsEncrypted: true),
            new SettingDefinition("Ui.Theme"));
        return catalog;
    }

    private static (EncryptingSettingStore Store, InMemorySettingStore Inner) CreateStore()
    {
        var inner = new InMemorySettingStore();
        return (new EncryptingSettingStore(inner, Catalog(), TestBlobEncryptor.CreateAesGcm()), inner);
    }

    [Fact]
    public async Task Encrypted_definitions_roundtrip_and_are_ciphertext_at_rest()
    {
        var (store, inner) = CreateStore();
        var scope = SettingScope.ForTenant(TenantId);

        await store.SetAsync("Smtp.Password", "s3cret!", scope);

        Assert.Equal("s3cret!", await store.GetOrNullAsync("Smtp.Password", scope));
        var atRest = await inner.GetOrNullAsync("Smtp.Password", scope);
        Assert.NotNull(atRest);
        Assert.NotEqual("s3cret!", atRest);
        Assert.DoesNotContain("s3cret", atRest);
    }

    [Fact]
    public async Task Plaintext_definitions_pass_through_untouched()
    {
        var (store, inner) = CreateStore();

        await store.SetAsync("Ui.Theme", "dark", SettingScope.Global);

        Assert.Equal("dark", await inner.GetOrNullAsync("Ui.Theme", SettingScope.Global));
        Assert.Equal("dark", await store.GetOrNullAsync("Ui.Theme", SettingScope.Global));
    }

    [Fact]
    public async Task GetAll_decrypts_only_encrypted_definitions()
    {
        var (store, _) = CreateStore();
        var scope = SettingScope.ForUserInTenant(TenantId, UserId);

        await store.SetAsync("Smtp.Password", "hunter2", scope);
        await store.SetAsync("Ui.Theme", "dark", scope);

        var all = await store.GetAllAsync(scope);
        Assert.Equal("hunter2", all["Smtp.Password"]);
        Assert.Equal("dark", all["Ui.Theme"]);
    }

    [Fact]
    public async Task Ciphertext_is_scope_bound()
    {
        var inner = new InMemorySettingStore();
        var store = new EncryptingSettingStore(inner, Catalog(), TestBlobEncryptor.CreateAesGcm());

        await store.SetAsync("Smtp.Password", "s3cret!", SettingScope.ForTenant(TenantId));

        var ciphertext = await inner.GetOrNullAsync("Smtp.Password", SettingScope.ForTenant(TenantId));
        var otherTenant = Guid.CreateVersion7();
        await inner.SetAsync("Smtp.Password", ciphertext, SettingScope.ForTenant(otherTenant));

        await Assert.ThrowsAnyAsync<Exception>(
            () => store.GetOrNullAsync("Smtp.Password", SettingScope.ForTenant(otherTenant)));
    }

    [Fact]
    public async Task A_settings_ciphertext_copied_to_another_settings_row_does_not_decrypt()
    {
        var (store, inner) = CreateStore();
        var scope = SettingScope.ForTenant(TenantId);
        var ct = TestContext.Current.CancellationToken;

        await store.SetAsync("Smtp.Password", "s3cret!", scope, ct);
        await store.SetAsync("Api.Token", "token-value", scope, ct);
        var passwordCiphertext = await inner.GetOrNullAsync("Smtp.Password", scope, ct);
        await inner.SetAsync("Api.Token", passwordCiphertext, scope, ct);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.GetOrNullAsync("Api.Token", scope, ct));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.GetAllAsync(scope, ct));
    }

    public static TheoryData<string> DestroyedKeys() => new()
    {
        "the scope's key was erased",
        "the value's key version was revoked",
    };

    [Theory]
    [MemberData(nameof(DestroyedKeys))]
    public async Task A_value_whose_key_was_destroyed_reads_as_absent_and_the_rest_of_the_scope_still_reads(string condition)
    {
        var ct = TestContext.Current.CancellationToken;
        var keyStore = new InMemoryKeyStore();
        var inner = new InMemorySettingStore();
        var logger = new FakeLogger<EncryptingSettingStore>();
        var store = new EncryptingSettingStore(inner, Catalog(), TestBlobEncryptor.CreateAesGcm(keyStore), logger);
        var scope = SettingScope.ForTenant(TenantId);
        var keyScope = new KeyScope(DataSensitivityLevel.TenantScoped, scope.TenantId);

        await store.SetAsync("Ui.Theme", "dark", scope, ct);
        await store.SetAsync("Smtp.Password", "s3cret!", scope, ct);
        if (condition == "the scope's key was erased")
        {
            await keyStore.EraseScopeAsync(keyScope, ct);
        }
        else
        {
            var keyId = (await keyStore.GetOrCreateCurrentKeyAsync(keyScope, ct)).KeyId;
            await keyStore.RevokeAsync(keyId, ct);
        }

        var all = await store.GetAllAsync(scope, ct);
        Assert.Equal("dark", Assert.Single(all).Value);
        Assert.Null(await store.GetOrNullAsync("Smtp.Password", scope, ct));
        Assert.Contains(logger.Collector.GetSnapshot(), record =>
            record.Id.Id == LogEvents.KeyManagement.EncryptedSettingUnreadable && record.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData("written before it was encrypted!")]
    [InlineData("abcd")]
    public async Task A_plaintext_row_of_an_encrypted_setting_fails_the_read(string plaintextRow)
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, inner) = CreateStore();
        var scope = SettingScope.ForTenant(TenantId);
        await store.SetAsync("Ui.Theme", "dark", scope, ct);
        await inner.SetAsync("Smtp.Password", plaintextRow, scope, ct);

        await Assert.ThrowsAnyAsync<Exception>(() => store.GetOrNullAsync("Smtp.Password", scope, ct));
        await Assert.ThrowsAnyAsync<Exception>(() => store.GetAllAsync(scope, ct));
    }

    [Fact]
    public async Task A_tampered_value_fails_the_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, inner) = CreateStore();
        var scope = SettingScope.ForTenant(TenantId);
        await store.SetAsync("Smtp.Password", "s3cret!", scope, ct);
        var bytes = Convert.FromBase64String((await inner.GetOrNullAsync("Smtp.Password", scope, ct))!);
        bytes[^1] ^= 0xFF;
        await inner.SetAsync("Smtp.Password", Convert.ToBase64String(bytes), scope, ct);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.GetOrNullAsync("Smtp.Password", scope, ct));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.GetAllAsync(scope, ct));
    }

    [Fact]
    public async Task Cancellation_while_reading_an_encrypted_value_is_not_treated_as_an_unreadable_value()
    {
        var (store, _) = CreateStore();
        var scope = SettingScope.ForTenant(TenantId);
        await store.SetAsync("Smtp.Password", "s3cret!", scope, TestContext.Current.CancellationToken);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetAllAsync(scope, cancelled.Token));
    }

    [Fact]
    public async Task Null_value_deletes_without_touching_the_encryptor()
    {
        var (store, inner) = CreateStore();
        var scope = SettingScope.Global;

        await store.SetAsync("Smtp.Password", "s3cret!", scope);
        await store.SetAsync("Smtp.Password", null, scope);

        Assert.Null(await inner.GetOrNullAsync("Smtp.Password", scope));
    }
}
