using Stratara.Abstractions.Settings;
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
    public async Task Null_value_deletes_without_touching_the_encryptor()
    {
        var (store, inner) = CreateStore();
        var scope = SettingScope.Global;

        await store.SetAsync("Smtp.Password", "s3cret!", scope);
        await store.SetAsync("Smtp.Password", null, scope);

        Assert.Null(await inner.GetOrNullAsync("Smtp.Password", scope));
    }
}
