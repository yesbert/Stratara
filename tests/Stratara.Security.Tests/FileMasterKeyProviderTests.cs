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

    [Fact]
    public void ShortKey_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Create(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));
        Assert.Contains("32", ex.Message);
    }

    [Fact]
    public async Task ValidKey_IsReturned()
    {
        var bytes = RandomNumberGenerator.GetBytes(48);
        var provider = Create(Convert.ToBase64String(bytes));

        var key = await provider.GetMasterKeyAsync();

        Assert.Equal(bytes, key.ToArray());
    }
}
