using System.Security.Cryptography;
using System.Text;
using Stratara.Abstractions.Security;
using Xunit;

namespace Stratara.Testing.Tests;

public class TestBlobEncryptorTests
{
    private static KeyScope Scope(Guid tenant) => new(DataSensitivityLevel.TenantScoped, tenant.ToString());

    [Fact]
    public async Task Roundtrips_a_blob_through_real_aes_gcm()
    {
        var keyStore = new InMemoryKeyStore();
        var encryptor = TestBlobEncryptor.CreateAesGcm(keyStore);
        var scope = Scope(Guid.CreateVersion7());
        var plaintext = Encoding.UTF8.GetBytes("the quick brown fox");

        await using var encrypted = await encryptor.EncryptAsync(new MemoryStream(plaintext), scope, "blob");
        await using var decrypted = await encryptor.DecryptAsync(encrypted, scope);

        using var buffer = new MemoryStream();
        await decrypted.CopyToAsync(buffer);
        Assert.Equal(plaintext, buffer.ToArray());
    }

    [Fact]
    public async Task Decrypting_under_a_different_scope_fails()
    {
        var keyStore = new InMemoryKeyStore();
        var encryptor = TestBlobEncryptor.CreateAesGcm(keyStore);
        var plaintext = Encoding.UTF8.GetBytes("secret");

        await using var encrypted = await encryptor.EncryptAsync(
            new MemoryStream(plaintext), Scope(Guid.CreateVersion7()), "blob");

        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
            await encryptor.DecryptAsync(encrypted, Scope(Guid.CreateVersion7())));
    }
}
