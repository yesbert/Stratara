using Microsoft.Extensions.Hosting;
using Moq;
using Stratara.Abstractions.Security;
using Xunit;

namespace Stratara.Security.Tests;

public class DummyKeyStoreTests
{
    private static readonly KeyScope Scope = new(DataSensitivityLevel.None);

    private static IHostEnvironment DevEnvironment()
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
        return env.Object;
    }

    private static IHostEnvironment NamedEnvironment(string name)
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(name);
        return env.Object;
    }

    [Fact]
    public async Task GetDataEncryptionKey_Returns32ByteKey()
    {
        var sut = new DummyKeyStore(DevEnvironment());

        var key = await sut.GetDataEncryptionKeyAsync("any-key-id");

        Assert.NotNull(key);
        Assert.Equal(32, key!.Length);
    }

    [Fact]
    public async Task GetDataEncryptionKey_DeterministicForSamePhrase()
    {
        var key1 = await new DummyKeyStore(DevEnvironment(), "my-phrase").GetDataEncryptionKeyAsync("id");
        var key2 = await new DummyKeyStore(DevEnvironment(), "my-phrase").GetDataEncryptionKeyAsync("id");

        Assert.Equal(key1, key2);
    }

    [Fact]
    public async Task GetDataEncryptionKey_DiffersForDifferentPhrase()
    {
        var key1 = await new DummyKeyStore(DevEnvironment(), "phrase-a").GetDataEncryptionKeyAsync("id");
        var key2 = await new DummyKeyStore(DevEnvironment(), "phrase-b").GetDataEncryptionKeyAsync("id");

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public async Task GetOrCreateCurrent_ReturnsFixedKeyId_RegardlessOfScope()
    {
        var sut = new DummyKeyStore(DevEnvironment());

        var a = await sut.GetOrCreateCurrentKeyAsync(new KeyScope(DataSensitivityLevel.None));
        var b = await sut.GetOrCreateCurrentKeyAsync(new KeyScope(DataSensitivityLevel.Confidential, "t", "u"));

        Assert.Equal(a.KeyId, b.KeyId);
        Assert.Equal(32, a.Key.Length);
    }

    [Fact]
    public async Task RevokeAndErase_AreNoOps()
    {
        var sut = new DummyKeyStore(DevEnvironment());

        var revoke = await Record.ExceptionAsync(async () => await sut.RevokeAsync("any"));
        var erase = await Record.ExceptionAsync(async () => await sut.EraseScopeAsync(Scope));

        Assert.Null(revoke);
        Assert.Null(erase);
    }

    [Fact]
    public void Constructor_DoesNotThrow_InDevelopment()
        => Assert.NotNull(new DummyKeyStore(DevEnvironment()));

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("QA")]
    [InlineData("Preview")]
    public void Constructor_Throws_OutsideDevelopment(string environmentName)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new DummyKeyStore(NamedEnvironment(environmentName)));

        Assert.Contains("Development", ex.Message);
        Assert.Contains(environmentName, ex.Message);
        Assert.Contains("real IKeyStore", ex.Message);
    }
}
