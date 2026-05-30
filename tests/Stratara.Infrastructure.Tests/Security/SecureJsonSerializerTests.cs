using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Infrastructure.Security.Serialization;
using Stratara.Abstractions.Security;

namespace Stratara.Infrastructure.Tests.Security;

[SuppressMessage(
    "Usage",
    "CA2263:Prefer generic overload when type is known",
    Justification = "Test intentionally exercises the non-generic by-Type Deserialize overload.")]
public class SecureJsonSerializerTests
{
    private readonly Mock<IKeyStore> _keyStoreMock = new();
    private readonly IEncryptionFactory _encryptionFactory = CreateEncryptionFactory();
    private readonly SecureJsonSerializer _serializer;

    private static readonly Guid TestTenantId = Guid.NewGuid();
    private static readonly Guid TestUserId = Guid.NewGuid();
    private static readonly string TestKeyId = "key-001";

    private static IEncryptionFactory CreateEncryptionFactory()
        => new ServiceCollection().AddStrataraBlobEncryption().BuildServiceProvider().GetRequiredService<IEncryptionFactory>();

    public SecureJsonSerializerTests()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);

        _keyStoreMock.Setup(k => k.GetOrCreateCurrentKeyAsync(
                It.IsAny<KeyScope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KeyMaterial(TestKeyId, key));

        _keyStoreMock.Setup(k => k.GetDataEncryptionKeyAsync(TestKeyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(key);

        _serializer = new SecureJsonSerializer(_keyStoreMock.Object, _encryptionFactory);
    }

    private sealed class PlainDto
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
    }

    [EncryptData(DataSensitivityLevel.TenantScoped)]
    private sealed class FullyEncryptedDto
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
    }

    private sealed class PartiallyEncryptedDto
    {
        public string Plain { get; set; } = "";

        [EncryptData(DataSensitivityLevel.UserScoped)]
        public string Secret { get; set; } = "";
    }

    [Fact]
    public async Task Serialize_PlainObject_ReturnsStandardJson()
    {
        var obj = new PlainDto { Name = "Test", Value = 42 };

        var result = await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);

        var parsed = JsonDocument.Parse(result).RootElement;
        Assert.Equal("Test", parsed.GetProperty("Name").GetString());
        Assert.Equal(42, parsed.GetProperty("Value").GetInt32());
    }

    [Fact]
    public async Task Serialize_ClassLevelEncryption_ReturnsEncryptedWrapper()
    {
        var obj = new FullyEncryptedDto { Name = "Secret", Value = 99 };

        var result = await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);

        var parsed = JsonDocument.Parse(result).RootElement;
        Assert.True(parsed.TryGetProperty("__enc", out _));
    }

    [Fact]
    public async Task Serialize_PropertyLevelEncryption_EncryptsOnlyMarkedProperties()
    {
        var obj = new PartiallyEncryptedDto { Plain = "visible", Secret = "hidden" };

        var result = await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);

        var parsed = JsonDocument.Parse(result).RootElement;
        Assert.Equal("visible", parsed.GetProperty("Plain").GetString());

        var secretProp = parsed.GetProperty("Secret");
        Assert.True(secretProp.TryGetProperty("__enc", out _));
    }

    [Fact]
    public async Task SerializeDeserialize_ClassLevel_RoundTrip()
    {
        var obj = new FullyEncryptedDto { Name = "RoundTrip", Value = 123 };

        var serialized = await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);
        var deserialized = await _serializer.DeserializeAsync<FullyEncryptedDto>(serialized, TestTenantId, TestUserId);

        Assert.NotNull(deserialized);
        Assert.Equal("RoundTrip", deserialized.Name);
        Assert.Equal(123, deserialized.Value);
    }

    [Fact]
    public async Task SerializeDeserialize_PropertyLevel_RoundTrip()
    {
        var obj = new PartiallyEncryptedDto { Plain = "visible", Secret = "sensitive" };

        var serialized = await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);
        var deserialized = await _serializer.DeserializeAsync<PartiallyEncryptedDto>(serialized, TestTenantId, TestUserId);

        Assert.NotNull(deserialized);
        Assert.Equal("visible", deserialized.Plain);
        Assert.Equal("sensitive", deserialized.Secret);
    }

    [Fact]
    public async Task SerializeDeserialize_PlainObject_RoundTrip()
    {
        var obj = new PlainDto { Name = "Plain", Value = 7 };

        var serialized = await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);
        var deserialized = await _serializer.DeserializeAsync<PlainDto>(serialized, TestTenantId, TestUserId);

        Assert.NotNull(deserialized);
        Assert.Equal("Plain", deserialized.Name);
        Assert.Equal(7, deserialized.Value);
    }

    [Fact]
    public async Task Deserialize_RevokedKey_ReturnsNull()
    {
        var obj = new FullyEncryptedDto { Name = "Revoked", Value = 1 };
        var serialized = await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);

        _keyStoreMock.Setup(k => k.GetDataEncryptionKeyAsync(TestKeyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        var deserialized = await _serializer.DeserializeAsync<FullyEncryptedDto>(serialized, TestTenantId, TestUserId);

        Assert.Null(deserialized);
    }

    [Fact]
    public async Task Serialize_CallsGetOrCreateCurrentKeyAsync_WithScope()
    {
        var obj = new FullyEncryptedDto { Name = "Test", Value = 1 };

        await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);

        _keyStoreMock.Verify(k => k.GetOrCreateCurrentKeyAsync(
            It.Is<KeyScope>(s =>
                s.Level == DataSensitivityLevel.TenantScoped &&
                s.TenantId == TestTenantId.ToString() &&
                s.UserId == TestUserId.ToString()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Serialize_ClassLevel_UsesClassScope()
    {
        var obj = new FullyEncryptedDto { Name = "Test", Value = 1 };

        var serialized = await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);
        var deserialized = await _serializer.DeserializeAsync<FullyEncryptedDto>(serialized, TestTenantId, TestUserId);

        Assert.NotNull(deserialized);
        Assert.Equal("Test", deserialized.Name);
    }

    [Fact]
    public async Task Serialize_PropertyLevel_EncryptsCorrectProperties()
    {
        var obj = new PartiallyEncryptedDto { Plain = "open", Secret = "closed" };

        var serialized = await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);

        var parsed = JsonDocument.Parse(serialized).RootElement;
        Assert.Equal("open", parsed.GetProperty("Plain").GetString());
        Assert.Equal(JsonValueKind.Object, parsed.GetProperty("Secret").ValueKind);
    }

    [Fact]
    public async Task Deserialize_DetectsEncryptedWrapper()
    {
        var obj = new FullyEncryptedDto { Name = "Detected", Value = 42 };
        var serialized = await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);

        var result = await _serializer.DeserializeAsync<FullyEncryptedDto>(serialized, TestTenantId, TestUserId);

        Assert.NotNull(result);
        Assert.Equal("Detected", result.Name);
    }

    [Fact]
    public async Task Deserialize_MixedEncryptedAndPlainProperties()
    {
        var obj = new PartiallyEncryptedDto { Plain = "mixed-plain", Secret = "mixed-secret" };

        var serialized = await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);
        var deserialized = await _serializer.DeserializeAsync<PartiallyEncryptedDto>(serialized, TestTenantId, TestUserId);

        Assert.NotNull(deserialized);
        Assert.Equal("mixed-plain", deserialized.Plain);
        Assert.Equal("mixed-secret", deserialized.Secret);
    }

    [Fact]
    public async Task Serialize_NullPropertyValue_WritesNull()
    {
        var obj = new PartiallyEncryptedDto { Plain = "text", Secret = null! };

        var serialized = await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);
        var parsed = JsonDocument.Parse(serialized).RootElement;

        Assert.Equal(JsonValueKind.Null, parsed.GetProperty("Secret").ValueKind);
    }

    [Fact]
    public async Task Serialize_ResolvesCurrentKeyOnce()
    {
        _keyStoreMock.Setup(k => k.GetOrCreateCurrentKeyAsync(It.IsAny<KeyScope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var key = new byte[32];
                RandomNumberGenerator.Fill(key);
                return new KeyMaterial(TestKeyId, key);
            });

        var obj = new FullyEncryptedDto { Name = "Zeroed", Value = 1 };
        await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);

        _keyStoreMock.Verify(k => k.GetOrCreateCurrentKeyAsync(It.IsAny<KeyScope>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Deserialize_NonEncryptedJson_ForEncryptedType_FallsBackToPlainDeserialize()
    {
        var plainJson = JsonSerializer.Serialize(new FullyEncryptedDto { Name = "Plain", Value = 5 });

        var result = await _serializer.DeserializeAsync<FullyEncryptedDto>(plainJson, TestTenantId, TestUserId);

        Assert.NotNull(result);
        Assert.Equal("Plain", result.Name);
        Assert.Equal(5, result.Value);
    }

    [Fact]
    public async Task Serialize_ClassLevel_RoundTrip()
    {
        var obj = new FullyEncryptedDto { Name = "RoundTrip", Value = 50 };

        var serialized = await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);
        var deserialized = await _serializer.DeserializeAsync<FullyEncryptedDto>(serialized, TestTenantId, TestUserId);

        Assert.NotNull(deserialized);
        Assert.Equal("RoundTrip", deserialized.Name);
        Assert.Equal(50, deserialized.Value);
    }

    [Fact]
    public async Task Deserialize_PropertyLevel_RevokedKey_ReturnsNullProperties()
    {
        var obj = new PartiallyEncryptedDto { Plain = "visible", Secret = "hidden" };
        var serialized = await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);

        _keyStoreMock.Setup(k => k.GetDataEncryptionKeyAsync(TestKeyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        var deserialized = await _serializer.DeserializeAsync<PartiallyEncryptedDto>(serialized, TestTenantId, TestUserId);

        Assert.NotNull(deserialized);
        Assert.Equal("visible", deserialized.Plain);
        Assert.Null(deserialized.Secret);
    }

    [Fact]
    public async Task SerializeAsync_Generic_NullThrows()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await _serializer.SerializeAsync<PlainDto>(null!, TestTenantId, TestUserId);
        });
    }

    [Fact]
    public async Task Deserialize_PropertyLevel_MissingPropertyInJson_ReturnsNull()
    {
        var obj = new PartiallyEncryptedDto { Plain = "present", Secret = "sensitive" };
        var serialized = await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);

        var parsed = JsonDocument.Parse(serialized);
        var root = parsed.RootElement;
        Assert.True(root.TryGetProperty("Secret", out var secretEl));
        Assert.Equal(JsonValueKind.Object, secretEl.ValueKind);

        var jsonWithoutPlain = $"{{\"Secret\":{secretEl.GetRawText()}}}";

        var deserialized = await _serializer.DeserializeAsync<PartiallyEncryptedDto>(jsonWithoutPlain, TestTenantId, TestUserId);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.Plain);
        Assert.Equal("sensitive", deserialized.Secret);
    }
}
