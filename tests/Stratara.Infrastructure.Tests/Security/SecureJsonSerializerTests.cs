using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Infrastructure.Security.Serialization;
using Stratara.Abstractions.Security;
using Stratara.Shared.EventSourcing;
using Stratara.Shared.EventSourcing.Extensions;
using Stratara.Testing;

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

    private sealed class EncryptedAmountDto
    {
        public string Plain { get; set; } = "";

        [EncryptData(DataSensitivityLevel.UserScoped)]
        public decimal Amount { get; set; }
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
    public async Task Deserialize_PropertyLevel_RevokedKeyOnAValueTypeField_ReadsAsDefault()
    {
        var obj = new EncryptedAmountDto { Plain = "visible", Amount = 42.5m };
        var serialized = await _serializer.SerializeAsync(obj, TestTenantId, TestUserId);

        _keyStoreMock.Setup(k => k.GetDataEncryptionKeyAsync(TestKeyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        var deserialized = await _serializer.DeserializeAsync<EncryptedAmountDto>(serialized, TestTenantId, TestUserId);

        Assert.NotNull(deserialized);
        Assert.Equal("visible", deserialized.Plain);
        Assert.Equal(0m, deserialized.Amount);
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

    private sealed class ErasableProfileDto
    {
        public string Language { get; set; } = "";

        [EncryptData(DataSensitivityLevel.UserScoped)]
        public string? Name { get; set; }

        [EncryptData(DataSensitivityLevel.UserScoped)]
        public int Age { get; set; }

        [EncryptData(DataSensitivityLevel.UserScoped)]
        public int? Height { get; set; }
    }

    [EncryptData(DataSensitivityLevel.UserScoped)]
    private sealed class NoteKeptWholeDto
    {
        public string Title { get; set; } = "";

        public string Body { get; set; } = "";
    }

    private sealed class NoteKeptFieldByFieldDto
    {
        public string Title { get; set; } = "";

        [EncryptData(DataSensitivityLevel.UserScoped)]
        public string Body { get; set; } = "";
    }

    private static SecureJsonSerializer SerializerOver(IKeyStore keys)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);
        return new SecureJsonSerializer(
            keys, CreateEncryptionFactory(), NullLogger<SecureJsonSerializer>.Instance, environment.Object);
    }

    [Fact]
    public async Task Deserialize_PropertyLevel_AfterTheUsersErasure_ReadsTheFieldsAbsentAndTheRestNormally()
    {
        var keys = new InMemoryKeyStore();
        var serializer = SerializerOver(keys);
        var userId = Guid.NewGuid();
        var serialized = await serializer.SerializeAsync(
            new ErasableProfileDto { Language = "en", Name = "Ada", Age = 36, Height = 165 }, TestTenantId, userId);

        await keys.MarkSubjectErasedAsync(KeySubject.User(userId.ToString("D")));
        await keys.EraseScopeAsync(new KeyScope(DataSensitivityLevel.UserScoped, TestTenantId.ToString(), userId.ToString()));

        var deserialized = await serializer.DeserializeAsync<ErasableProfileDto>(serialized, TestTenantId, userId);

        Assert.NotNull(deserialized);
        Assert.Equal("en", deserialized.Language);
        Assert.Null(deserialized.Name);
        Assert.Equal(0, deserialized.Age);
        Assert.Null(deserialized.Height);
    }

    [Fact]
    public async Task Deserialize_AWholeTypePayload_IntoATypeThatNowMarksItsProperties_ReadsIt()
    {
        var keys = new InMemoryKeyStore();
        var serializer = SerializerOver(keys);
        var written = await serializer.SerializeAsync(
            new NoteKeptWholeDto { Title = "Plan", Body = "secret" }, TestTenantId, TestUserId);

        var read = await serializer.DeserializeAsync<NoteKeptFieldByFieldDto>(written, TestTenantId, TestUserId);

        Assert.NotNull(read);
        Assert.Equal("Plan", read.Title);
        Assert.Equal("secret", read.Body);
    }

    private enum Shade
    {
        Light,
        Dark,
    }

    private sealed class AnnotatedDto
    {
        [JsonPropertyName("ref")]
        public string Reference { get; set; } = "";

        [JsonIgnore]
        public string Scratch { get; set; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Note { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public Shade Shade { get; set; }

        [EncryptData(DataSensitivityLevel.UserScoped)]
        public string? Secret { get; set; }
    }

    [Fact]
    public async Task SerializeDeserialize_PropertyLevel_ARenamedPropertyRoundTrips()
    {
        var written = await _serializer.SerializeAsync(
            new AnnotatedDto { Reference = "R-1", Secret = "s" }, TestTenantId, TestUserId, TestContext.Current.CancellationToken);

        var read = await _serializer.DeserializeAsync<AnnotatedDto>(written, TestTenantId, TestUserId, TestContext.Current.CancellationToken);

        Assert.Equal("R-1", read!.Reference);
        Assert.Equal("s", read.Secret);
    }

    /// <summary>
    /// With the marked property empty the stored form carries no ciphertext at all, so the read takes
    /// a different branch from the one above — and has to map the names just the same.
    /// </summary>
    [Fact]
    public async Task SerializeDeserialize_PropertyLevel_ARenamedPropertyRoundTripsWhenNothingIsEncrypted()
    {
        var written = await _serializer.SerializeAsync(
            new AnnotatedDto { Reference = "R-2", Secret = null }, TestTenantId, TestUserId, TestContext.Current.CancellationToken);

        var read = await _serializer.DeserializeAsync<AnnotatedDto>(written, TestTenantId, TestUserId, TestContext.Current.CancellationToken);

        Assert.Equal("R-2", read!.Reference);
        Assert.Null(read.Secret);
    }

    /// <summary>
    /// The stored names are what an event upcaster works on, so they stay the property names even
    /// where the type renames a property for ordinary JSON.
    /// </summary>
    [Fact]
    public async Task Serialize_PropertyLevel_StoresPropertiesUnderTheirClrNames()
    {
        var written = await _serializer.SerializeAsync(
            new AnnotatedDto { Reference = "R-3", Secret = "s" }, TestTenantId, TestUserId, TestContext.Current.CancellationToken);

        var root = JsonDocument.Parse(written).RootElement;
        Assert.Equal("R-3", root.GetProperty("Reference").GetString());
        Assert.False(root.TryGetProperty("ref", out _));
    }

    [Fact]
    public async Task Serialize_PropertyLevel_LeavesOutAnIgnoredProperty()
    {
        var written = await _serializer.SerializeAsync(
            new AnnotatedDto { Reference = "R-4", Scratch = "never-stored", Secret = "s" },
            TestTenantId, TestUserId, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("never-stored", written, StringComparison.Ordinal);
        Assert.False(JsonDocument.Parse(written).RootElement.TryGetProperty("Scratch", out _));
    }

    [Fact]
    public async Task Serialize_PropertyLevel_HonoursAConditionalIgnore()
    {
        var withoutNote = await _serializer.SerializeAsync(
            new AnnotatedDto { Secret = "s", Note = null }, TestTenantId, TestUserId, TestContext.Current.CancellationToken);
        var withNote = await _serializer.SerializeAsync(
            new AnnotatedDto { Secret = "s", Note = "kept" }, TestTenantId, TestUserId, TestContext.Current.CancellationToken);

        Assert.False(JsonDocument.Parse(withoutNote).RootElement.TryGetProperty("Note", out _));
        var read = await _serializer.DeserializeAsync<AnnotatedDto>(withNote, TestTenantId, TestUserId, TestContext.Current.CancellationToken);
        Assert.Equal("kept", read!.Note);
    }

    [Fact]
    public async Task Serialize_PropertyLevel_WritesAPropertyThroughItsOwnConverter()
    {
        var written = await _serializer.SerializeAsync(
            new AnnotatedDto { Shade = Shade.Dark, Secret = "s" }, TestTenantId, TestUserId, TestContext.Current.CancellationToken);

        Assert.Equal("Dark", JsonDocument.Parse(written).RootElement.GetProperty("Shade").GetString());
        var read = await _serializer.DeserializeAsync<AnnotatedDto>(written, TestTenantId, TestUserId, TestContext.Current.CancellationToken);
        Assert.Equal(Shade.Dark, read!.Shade);
    }

    /// <summary>
    /// Rows already stored carry every property under its CLR name, an ignored one included, and the
    /// converted property in its default form. They have to keep reading.
    /// </summary>
    [Fact]
    public async Task Deserialize_PropertyLevel_APayloadInTheEarlierShape_StillReads()
    {
        var encrypted = await _serializer.SerializeAsync(
            new AnnotatedDto { Secret = "old-secret" }, TestTenantId, TestUserId, TestContext.Current.CancellationToken);
        var secret = JsonDocument.Parse(encrypted).RootElement.GetProperty("Secret").GetRawText();
        var earlier = $$"""{"Reference":"R-old","Scratch":"stored-once","Note":null,"Shade":1,"Secret":{{secret}}}""";

        var read = await _serializer.DeserializeAsync<AnnotatedDto>(earlier, TestTenantId, TestUserId, TestContext.Current.CancellationToken);

        Assert.Equal("R-old", read!.Reference);
        Assert.Equal("old-secret", read.Secret);
        Assert.Equal(Shade.Dark, read.Shade);
        Assert.Equal("", read.Scratch);
    }

    [Fact]
    public async Task Deserialize_PropertyLevel_OrdinaryJsonUnderTheContractNames_StillReads()
    {
        var ordinary = JsonSerializer.Serialize(new AnnotatedDto { Reference = "R-plain", Shade = Shade.Dark });

        var read = await _serializer.DeserializeAsync<AnnotatedDto>(ordinary, TestTenantId, TestUserId, TestContext.Current.CancellationToken);

        Assert.Equal("R-plain", read!.Reference);
        Assert.Equal(Shade.Dark, read.Shade);
    }

    private sealed record HomeAddress(string Street, int Number);

    private sealed class MarkedCustomer
    {
        [EncryptData(DataSensitivityLevel.TenantScoped)]
        public string? Email { get; set; }

        [EncryptData]
        public string? Phone { get; set; }

        [EncryptData]
        public int? Age { get; set; }

        [EncryptData]
        public Guid Reference { get; set; }

        [EncryptData]
        public HomeAddress? Home { get; set; }

        [EncryptData]
        public List<string> Tags { get; set; } = [];

        [EncryptData]
        [JsonConverter(typeof(JsonStringEnumConverter<Shade>))]
        public Shade Tint { get; set; }

        public string? City { get; set; }
    }

    private sealed class OnePropertyMarked
    {
        [EncryptData(DataSensitivityLevel.TenantScoped)]
        public string? Email { get; set; }
    }

    private sealed record ConstructorMarkedCustomer([EncryptData(DataSensitivityLevel.TenantScoped)] string Email, string City);

    private sealed class UnmarkedCustomer
    {
        public string? City { get; set; }
    }

    private SecureJsonSerializer CreateSerializer(IKeyStore keyStore) =>
        new(keyStore, _encryptionFactory, NullLogger<SecureJsonSerializer>.Instance);

    private static JsonElement StoredValue(string json) => JsonDocument.Parse(json).RootElement.GetProperty("NewValue");

    [Fact]
    public void Metadata_RecordedChangeOfAnAggregateWithMarks_BorrowsThem()
    {
        var metadata = MetadataCache.GetOrCreateMetadata(typeof(FieldChangedEvent<MarkedCustomer>));

        Assert.True(metadata.RequiresEncryption);
        Assert.Same(MetadataCache.GetOrCreateMetadata(typeof(MarkedCustomer)), metadata.RecordedAggregate);
        Assert.Contains(nameof(MarkedCustomer.Email), metadata.RecordedAggregate!.EncryptedPropertiesByName.Keys);
        Assert.DoesNotContain(nameof(MarkedCustomer.City), metadata.RecordedAggregate.EncryptedPropertiesByName.Keys);
    }

    [Fact]
    public void Metadata_RecordedChangeOfAnAggregateMarkedThroughItsConstructor_BorrowsThem()
    {
        var metadata = MetadataCache.GetOrCreateMetadata(typeof(FieldChangedEvent<ConstructorMarkedCustomer>));

        Assert.True(metadata.RequiresEncryption);
        Assert.Contains(nameof(ConstructorMarkedCustomer.Email), metadata.RecordedAggregate!.EncryptedPropertiesByName.Keys);
    }

    [Fact]
    public void Metadata_RecordedChangeOfAnAggregateWithoutMarks_RequiresNothing()
    {
        var metadata = MetadataCache.GetOrCreateMetadata(typeof(FieldChangedEvent<UnmarkedCustomer>));

        Assert.False(metadata.RequiresEncryption);
        Assert.NotNull(metadata.RecordedAggregate);
    }

    [Fact]
    public void Metadata_AnyOtherType_HasNoRecordedAggregate()
    {
        Assert.Null(MetadataCache.GetOrCreateMetadata(typeof(MarkedCustomer)).RecordedAggregate);
    }

    [Fact]
    public async Task Serialize_RecordedChangeToAMarkedProperty_WritesTheNamePlainAndTheValueAsAnEnvelope()
    {
        var json = await _serializer.SerializeAsync(
            new FieldChangedEvent<MarkedCustomer>(nameof(MarkedCustomer.Email), "ada@secret.example"), TestTenantId, TestUserId);

        Assert.DoesNotContain("ada@secret.example", json, StringComparison.Ordinal);
        Assert.Equal(nameof(MarkedCustomer.Email), JsonDocument.Parse(json).RootElement.GetProperty("PropertyName").GetString());
        Assert.True(StoredValue(json).GetProperty(SecurityConstants.EncryptionMarker).GetBoolean());
    }

    [Fact]
    public async Task Serialize_RecordedChangeToAMarkedProperty_RequestsTheKeyTheAggregatesOwnSerializationRequests()
    {
        await _serializer.SerializeAsync(new OnePropertyMarked { Email = "a" }, TestTenantId, TestUserId);
        await _serializer.SerializeAsync(
            new FieldChangedEvent<OnePropertyMarked>(nameof(OnePropertyMarked.Email), "b"), TestTenantId, TestUserId);

        var scopes = _keyStoreMock.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IKeyStore.GetOrCreateCurrentKeyAsync))
            .Select(invocation => (KeyScope)invocation.Arguments[0]!)
            .ToList();
        Assert.Equal(2, scopes.Count);
        Assert.Equal(scopes[0], scopes[1]);
        Assert.Equal(new KeyScope(DataSensitivityLevel.TenantScoped, TestTenantId.ToString(), TestUserId.ToString()), scopes[1]);
    }

    [Fact]
    public async Task Serialize_RecordedChangeToAPropertyMarkedThroughTheConstructor_IsProtected()
    {
        var json = await _serializer.SerializeAsync(
            new FieldChangedEvent<ConstructorMarkedCustomer>(nameof(ConstructorMarkedCustomer.Email), "ada@secret.example"), TestTenantId, TestUserId);

        Assert.DoesNotContain("ada@secret.example", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serialize_RecordedChangeThatClearsAMarkedProperty_WritesNullWithoutAKey()
    {
        var change = new FieldChangedEvent<MarkedCustomer>(nameof(MarkedCustomer.Email), null);

        var json = await _serializer.SerializeAsync(change, TestTenantId, TestUserId);

        Assert.Equal(JsonSerializer.Serialize(change), json);
        _keyStoreMock.Verify(k => k.GetOrCreateCurrentKeyAsync(It.IsAny<KeyScope>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Serialize_RecordedChangeToAnUnmarkedProperty_IsByteIdenticalAndAsksForNoKey()
    {
        var change = new FieldChangedEvent<MarkedCustomer>(nameof(MarkedCustomer.City), "Berlin");

        var json = await _serializer.SerializeAsync(change, TestTenantId, TestUserId);

        Assert.Equal(JsonSerializer.Serialize(change), json);
        _keyStoreMock.Verify(k => k.GetOrCreateCurrentKeyAsync(It.IsAny<KeyScope>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Serialize_RecordedChangeOfAnAggregateWithoutMarks_IsByteIdenticalAndAsksForNoKey()
    {
        var change = new FieldChangedEvent<UnmarkedCustomer>(nameof(UnmarkedCustomer.City), "Berlin");

        var json = await _serializer.SerializeAsync(change, TestTenantId, TestUserId);

        Assert.Equal(JsonSerializer.Serialize(change), json);
        _keyStoreMock.Verify(k => k.GetOrCreateCurrentKeyAsync(It.IsAny<KeyScope>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Serialize_RecordedChangeForAnErasedOwner_IsRefusedByTheKeyStore()
    {
        var keyStore = new InMemoryKeyStore();
        await keyStore.MarkSubjectErasedAsync(KeySubject.User(TestUserId.ToString()));

        await Assert.ThrowsAsync<KeyScopeErasedException>(() => CreateSerializer(keyStore).SerializeAsync(
            new FieldChangedEvent<MarkedCustomer>(nameof(MarkedCustomer.Phone), "1"), TestTenantId, TestUserId));
    }

    [Fact]
    public async Task Serialize_RecordedChangeWithNothingToIsolateBy_IsRefusedOutsideDevelopment()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _serializer.SerializeAsync(
            new FieldChangedEvent<MarkedCustomer>(nameof(MarkedCustomer.Phone), "1"), Guid.Empty, Guid.Empty));
    }

    private async Task<object?> RoundTripAsync(string propertyName, object value)
    {
        var json = await _serializer.SerializeAsync(new FieldChangedEvent<MarkedCustomer>(propertyName, value), TestTenantId, TestUserId);
        Assert.True(StoredValue(json).TryGetProperty(SecurityConstants.EncryptionMarker, out _));

        var read = await _serializer.DeserializeAsync<FieldChangedEvent<MarkedCustomer>>(json, TestTenantId, TestUserId);

        Assert.Equal(propertyName, read!.PropertyName);
        var aggregate = new MarkedCustomer();
        aggregate.ApplyPropertyChanged(read);
        return typeof(MarkedCustomer).GetProperty(propertyName)!.GetValue(aggregate);
    }

    [Fact]
    public async Task RoundTrip_RecordedChange_String() =>
        Assert.Equal("ada", await RoundTripAsync(nameof(MarkedCustomer.Email), "ada"));

    [Fact]
    public async Task RoundTrip_RecordedChange_NullableInt() =>
        Assert.Equal(36, await RoundTripAsync(nameof(MarkedCustomer.Age), 36));

    [Fact]
    public async Task RoundTrip_RecordedChange_NonNullableGuid()
    {
        var reference = Guid.CreateVersion7();

        Assert.Equal(reference, await RoundTripAsync(nameof(MarkedCustomer.Reference), reference));
    }

    [Fact]
    public async Task RoundTrip_RecordedChange_Record()
    {
        var home = new HomeAddress("Main", 7);

        Assert.Equal(home, await RoundTripAsync(nameof(MarkedCustomer.Home), home));
    }

    [Fact]
    public async Task RoundTrip_RecordedChange_ACollectionOfAnotherShapeThanTheProperty()
    {
        string[] submitted = ["a", "b"];

        Assert.Equal<IEnumerable<string>>(submitted, (List<string>)(await RoundTripAsync(nameof(MarkedCustomer.Tags), submitted))!);
    }

    [Fact]
    public async Task RoundTrip_RecordedChange_APropertyWithItsOwnConverter_ReadsBackLikeAnUnmarkedChange() =>
        Assert.Equal(Shade.Dark, await RoundTripAsync(nameof(MarkedCustomer.Tint), Shade.Dark));

    [Fact]
    public async Task Deserialize_RecordedChangeWrittenInTheClear_ForAMarkedProperty_ReadsAsStored()
    {
        var earlier = JsonSerializer.Serialize(new FieldChangedEvent<MarkedCustomer>(nameof(MarkedCustomer.Email), "earlier"));

        var read = await _serializer.DeserializeAsync<FieldChangedEvent<MarkedCustomer>>(earlier, TestTenantId, TestUserId);

        Assert.Equal("earlier", Assert.IsType<JsonElement>(read!.NewValue).GetString());
        _keyStoreMock.Verify(k => k.GetDataEncryptionKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Deserialize_RecordedChangeThatClearedAMarkedProperty_ReadsAsAbsent()
    {
        var read = await _serializer.DeserializeAsync<FieldChangedEvent<MarkedCustomer>>(
            JsonSerializer.Serialize(new FieldChangedEvent<MarkedCustomer>(nameof(MarkedCustomer.Email), null)), TestTenantId, TestUserId);

        Assert.Null(read!.NewValue);
    }

    [Fact]
    public async Task Deserialize_RecordedCiphertext_AfterEveryMarkWasRemoved_IsStillDecrypted()
    {
        var json = await _serializer.SerializeAsync(
            new FieldChangedEvent<MarkedCustomer>(nameof(MarkedCustomer.Email), "ada"), TestTenantId, TestUserId);

        var read = await _serializer.DeserializeAsync<FieldChangedEvent<UnmarkedCustomer>>(json, TestTenantId, TestUserId);

        Assert.Equal("ada", Assert.IsType<JsonElement>(read!.NewValue).GetString());
    }

    [Fact]
    public async Task Deserialize_RecordedCiphertext_AfterTheKeyVersionIsRevoked_ReadsAsAbsent()
    {
        var keyStore = new InMemoryKeyStore();
        var sut = CreateSerializer(keyStore);
        var json = await sut.SerializeAsync(new FieldChangedEvent<MarkedCustomer>(nameof(MarkedCustomer.Phone), "1"), TestTenantId, TestUserId);

        await keyStore.RevokeAsync(StoredValue(json).GetProperty("kid").GetString()!);
        var read = await sut.DeserializeAsync<FieldChangedEvent<MarkedCustomer>>(json, TestTenantId, TestUserId);

        Assert.Equal(nameof(MarkedCustomer.Phone), read!.PropertyName);
        Assert.Null(read.NewValue);
    }

    [Fact]
    public async Task Deserialize_RecordedCiphertext_AfterTheSubjectIsErased_ReadsAsAbsent()
    {
        var keyStore = new InMemoryKeyStore();
        var sut = CreateSerializer(keyStore);
        var json = await sut.SerializeAsync(new FieldChangedEvent<MarkedCustomer>(nameof(MarkedCustomer.Phone), "1"), TestTenantId, TestUserId);

        await keyStore.EraseScopeAsync(new KeyScope(DataSensitivityLevel.UserScoped, TestTenantId.ToString(), TestUserId.ToString()));
        await keyStore.MarkSubjectErasedAsync(KeySubject.User(TestUserId.ToString()));
        var read = await sut.DeserializeAsync<FieldChangedEvent<MarkedCustomer>>(json, TestTenantId, TestUserId);

        Assert.Equal(nameof(MarkedCustomer.Phone), read!.PropertyName);
        Assert.Null(read.NewValue);
    }

    [Theory]
    [InlineData(nameof(MarkedCustomer.Phone))]
    [InlineData(nameof(MarkedCustomer.City))]
    public async Task Deserialize_RecordedCiphertext_UnderAnAlteredPropertyName_FailsAuthentication(string alteredName)
    {
        var json = await _serializer.SerializeAsync(
            new FieldChangedEvent<MarkedCustomer>(nameof(MarkedCustomer.Email), "ada"), TestTenantId, TestUserId);
        var altered = json.Replace($"\"{nameof(MarkedCustomer.Email)}\"", $"\"{alteredName}\"", StringComparison.Ordinal);

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => _serializer.DeserializeAsync<FieldChangedEvent<MarkedCustomer>>(altered, TestTenantId, TestUserId));
    }

    [Fact]
    public async Task Deserialize_RecordedCiphertext_WithoutAPropertyName_FailsAuthentication()
    {
        var json = await _serializer.SerializeAsync(
            new FieldChangedEvent<MarkedCustomer>(nameof(MarkedCustomer.Email), "ada"), TestTenantId, TestUserId);
        var nameless = $$"""{"NewValue":{{StoredValue(json).GetRawText()}}}""";

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => _serializer.DeserializeAsync<FieldChangedEvent<MarkedCustomer>>(nameless, TestTenantId, TestUserId));
    }

    [Fact]
    public async Task Deserialize_ASnapshotEnvelopePastedIntoARecordedChange_DoesNotDecrypt()
    {
        var snapshot = await _serializer.SerializeAsync(new OnePropertyMarked { Email = "ada" }, TestTenantId, TestUserId);
        var envelope = JsonDocument.Parse(snapshot).RootElement.GetProperty(nameof(OnePropertyMarked.Email)).GetRawText();
        var pasted = $$"""{"PropertyName":"{{nameof(OnePropertyMarked.Email)}}","NewValue":{{envelope}}}""";

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => _serializer.DeserializeAsync<FieldChangedEvent<OnePropertyMarked>>(pasted, TestTenantId, TestUserId));
    }

    [Fact]
    public async Task Deserialize_ARecordedEnvelopePastedIntoASnapshot_DoesNotDecrypt()
    {
        var change = await _serializer.SerializeAsync(
            new FieldChangedEvent<OnePropertyMarked>(nameof(OnePropertyMarked.Email), "ada"), TestTenantId, TestUserId);
        var pasted = $$"""{"{{nameof(OnePropertyMarked.Email)}}":{{StoredValue(change).GetRawText()}}}""";

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => _serializer.DeserializeAsync<OnePropertyMarked>(pasted, TestTenantId, TestUserId));
    }
}
