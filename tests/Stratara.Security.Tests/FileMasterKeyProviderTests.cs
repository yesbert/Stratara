using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stratara.Security.Tests;

public class FileMasterKeyProviderTests
{
    private static FileMasterKeyProvider Create(string? masterKeyBase64)
        => new(Options.Create(new StrataraFileKeyStoreOptions { MasterKeyBase64 = masterKeyBase64 }));

    [Fact]
    public void MissingKey_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Create(null));
        Assert.Contains("openssl rand", ex.Message);
    }

    [Fact]
    public void InvalidBase64_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Create("not valid base64 !!!"));
        Assert.Contains("base64", ex.Message);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(31)]
    [InlineData(33)]
    public void NonAes256Length_Throws(int keyBytes)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Create(Convert.ToBase64String(RandomNumberGenerator.GetBytes(keyBytes))));
        Assert.Contains("32", ex.Message);
        Assert.Contains("openssl rand", ex.Message);
    }

    [Fact]
    public void OversizeKey_RejectedAtConstruction()
    {
        // A 48-byte KEK (e.g. `openssl rand -base64 48`, common for HKDF master keys) is NOT a
        // valid AES key size. Before the fix it passed construction and the startup probe, then
        // crashed inside AesGcm on the first key creation. It must now be rejected at boot.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Create(Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))));
        Assert.Contains("48 bytes", ex.Message);
        Assert.Contains("openssl rand -base64 32", ex.Message);
    }

    [Fact]
    public async Task ValidAes256Key_IsReturned()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var provider = Create(Convert.ToBase64String(bytes));

        var key = await provider.GetMasterKeyAsync();

        Assert.Equal(bytes, key.ToArray());
    }
}
