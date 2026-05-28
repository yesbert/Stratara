using Microsoft.Extensions.Hosting;
using Moq;
using Stratara.Infrastructure.Security.KeyManagement;
using Stratara.Abstractions.Security;

namespace Stratara.Infrastructure.Tests.Security;

public class DummyKeyStoreTests
{
    private static IHostEnvironment DevEnvironment()
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
        return env.Object;
    }

    private static IHostEnvironment ProductionEnvironment()
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);
        return env.Object;
    }

    [Fact]
    public async Task GetDataEncryptionKeyAsync_Returns32ByteKey()
    {
        var sut = new DummyKeyStore(DevEnvironment());

        var key = await sut.GetDataEncryptionKeyAsync("any-key-id");

        Assert.NotNull(key);
        Assert.Equal(32, key!.Length);
    }

    [Fact]
    public async Task GetDataEncryptionKeyAsync_ReturnsSameKeyForAnyKeyId()
    {
        var sut = new DummyKeyStore(DevEnvironment());

        var key1 = await sut.GetDataEncryptionKeyAsync("key-1");
        var key2 = await sut.GetDataEncryptionKeyAsync("key-2");

        Assert.Equal(key1, key2);
    }

    [Fact]
    public async Task GetDataEncryptionKeyAsync_DeterministicForSamePhrase()
    {
        var sut1 = new DummyKeyStore(DevEnvironment(), "my-phrase");
        var sut2 = new DummyKeyStore(DevEnvironment(), "my-phrase");

        var key1 = await sut1.GetDataEncryptionKeyAsync("id");
        var key2 = await sut2.GetDataEncryptionKeyAsync("id");

        Assert.Equal(key1, key2);
    }

    [Fact]
    public async Task GetDataEncryptionKeyAsync_DifferentKeyForDifferentPhrase()
    {
        var sut1 = new DummyKeyStore(DevEnvironment(), "phrase-a");
        var sut2 = new DummyKeyStore(DevEnvironment(), "phrase-b");

        var key1 = await sut1.GetDataEncryptionKeyAsync("id");
        var key2 = await sut2.GetDataEncryptionKeyAsync("id");

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public async Task EnsureKeyAsync_ReturnsFixedKeyId()
    {
        var sut = new DummyKeyStore(DevEnvironment());

        var keyId = await sut.EnsureKeyAsync(DataSensitivityLevel.None, null, null);

        Assert.Equal("dummy-test-key-id", keyId);
    }

    [Fact]
    public async Task EnsureKeyAsync_ReturnsSameKeyId_RegardlessOfParameters()
    {
        var sut = new DummyKeyStore(DevEnvironment());

        var keyId1 = await sut.EnsureKeyAsync(DataSensitivityLevel.None, null, null);
        var keyId2 = await sut.EnsureKeyAsync(DataSensitivityLevel.Confidential, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(keyId1, keyId2);
    }

    [Fact]
    public async Task RevokeAsync_CompletesWithoutError()
    {
        var sut = new DummyKeyStore(DevEnvironment());

        var exception = await Record.ExceptionAsync(async () => await sut.RevokeAsync("any-key-id"));

        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_Throws_InProductionEnvironment()
    {
        Assert.Throws<InvalidOperationException>(() => new DummyKeyStore(ProductionEnvironment()));
    }

    [Fact]
    public void Constructor_Throws_InProductionEnvironment_WithCustomPhrase()
    {
        Assert.Throws<InvalidOperationException>(() => new DummyKeyStore(ProductionEnvironment(), "any-phrase"));
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("QA")]
    [InlineData("UAT")]
    [InlineData("Preview")]
    [InlineData("Stage")]
    [InlineData("Test")]
    public void Constructor_Throws_InAnyNonDevelopmentEnvironment(string environmentName)
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environmentName);

        var ex = Assert.Throws<InvalidOperationException>(() => new DummyKeyStore(env.Object));

        Assert.Contains("Development", ex.Message);
        Assert.Contains(environmentName, ex.Message);
        Assert.Contains("AddSecurity", ex.Message);
    }

    [Fact]
    public void Constructor_DoesNotThrow_InDevelopmentEnvironment()
    {
        var sut = new DummyKeyStore(DevEnvironment());

        Assert.NotNull(sut);
    }
}
