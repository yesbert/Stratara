using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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

        _serializer = CreateSerializer(Environments.Production);
    }

    private SecureJsonSerializer CreateSerializer(string environmentName)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        return new SecureJsonSerializer(
            _keyStoreMock.Object, _encryptionFactory, NullLogger<SecureJsonSerializer>.Instance, environment.Object);
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

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Serialize_TenantScopedWithNothingToIsolateBy_IsRefusedOutsideDevelopment(string? tenantId)
    {
        var sut = CreateSerializer(Environments.Production);
        var tenant = tenantId is null ? (Guid?)null : Guid.Parse(tenantId);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SerializeAsync(new FullyEncryptedDto { Name = "n", Value = 1 }, tenant, Guid.Empty));

        Assert.Contains("TenantScoped", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("TenantId", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Confidential", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mirror of the user-scoped case: a tenant-scoped value carrying only a user still resolves
    /// to a distinct scope per user, so it isolates. Refusing it would be refusing a coarser shape,
    /// not an absent one.
    /// </summary>
    [Fact]
    public async Task Serialize_TenantScopedWithAUserButNoTenant_IsAccepted()
    {
        var sut = CreateSerializer(Environments.Production);

        var json = await sut.SerializeAsync(
            new FullyEncryptedDto { Name = "n", Value = 1 }, Guid.Empty, Guid.CreateVersion7());

        Assert.Contains(SecurityConstants.EncryptionMarker, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Serialize_UserScopedWithNeitherUserNorTenant_IsRefusedOutsideDevelopment(string? userId)
    {
        var sut = CreateSerializer(Environments.Production);
        var user = userId is null ? (Guid?)null : Guid.Parse(userId);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SerializeAsync(new PartiallyEncryptedDto { Plain = "p", Secret = "s" }, Guid.Empty, user));

        Assert.Contains("UserScoped", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Confidential", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Not the degenerate case, and the framework relies on it: the scope resolves to
    /// "UserScoped:&lt;tenant&gt;:", which still separates tenants. Coarser than the level's name,
    /// but isolation rather than its absence — an event's payload is bound to its stream's owner
    /// exactly this way.
    /// </summary>
    [Fact]
    public async Task Serialize_UserScopedWithATenantButNoUser_IsAccepted()
    {
        var sut = CreateSerializer(Environments.Production);

        var json = await sut.SerializeAsync(
            new PartiallyEncryptedDto { Plain = "p", Secret = "s" }, Guid.CreateVersion7(), null);

        Assert.Contains(SecurityConstants.EncryptionMarker, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serialize_TenantScopedWithoutATenant_ProceedsInDevelopment()
    {
        var sut = CreateSerializer(Environments.Development);

        var json = await sut.SerializeAsync(new FullyEncryptedDto { Name = "n", Value = 1 }, Guid.Empty, Guid.Empty);

        Assert.Contains(SecurityConstants.EncryptionMarker, json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The difference between a guard and a data-loss bug. Values written into the degenerate scope
    /// before the refusal existed have to stay readable — refusing on the read path would destroy
    /// access to them rather than protect anything.
    /// </summary>
    [Fact]
    public async Task Deserialize_ValueWrittenIntoTheDegenerateScope_StillReadsOutsideDevelopment()
    {
        var written = await CreateSerializer(Environments.Development)
            .SerializeAsync(new FullyEncryptedDto { Name = "written-before-the-guard", Value = 7 }, Guid.Empty, Guid.Empty);

        var read = await CreateSerializer(Environments.Production)
            .DeserializeAsync<FullyEncryptedDto>(written, Guid.Empty, Guid.Empty);

        Assert.Equal("written-before-the-guard", read!.Name);
        Assert.Equal(7, read.Value);
    }

    /// <summary>
    /// The migration this guard points at. `Confidential` claims one system-wide key and no
    /// isolation, so the scope guard does not fire for it — an absent tenant is what that level is
    /// for. The empty identifier is what a tenant-less aggregate actually supplies: an event row's
    /// tenant is not nullable, so the value is empty rather than missing.
    /// </summary>
    [Fact]
    public async Task Serialize_ConfidentialWithoutATenant_IsAccepted()
    {
        var sut = CreateSerializer(Environments.Production);

        var json = await sut.SerializeAsync(new ConfidentialDto { Secret = "s" }, Guid.Empty, Guid.Empty);

        Assert.Contains(SecurityConstants.EncryptionMarker, json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A separate, older guard: the additional authenticated data is built from the tenant whatever
    /// the level, so an explicitly null tenant is refused for `Confidential` too. Pinned here because
    /// the migration advice would otherwise read as "pass nothing", which does not work.
    /// </summary>
    [Fact]
    public async Task Serialize_ConfidentialWithAnExplicitlyNullTenant_IsStillRefusedByTheAadGuard()
    {
        var sut = CreateSerializer(Environments.Production);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SerializeAsync(new ConfidentialDto { Secret = "s" }, null, null));

        Assert.Contains("null TenantId", thrown.Message, StringComparison.Ordinal);
    }

    [EncryptData(DataSensitivityLevel.Confidential)]
    private sealed class ConfidentialDto
    {
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
