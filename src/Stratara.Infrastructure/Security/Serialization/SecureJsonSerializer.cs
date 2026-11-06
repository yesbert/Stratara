using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stratara.Diagnostics;
using Stratara.Infrastructure.Security.Mapping;
using Stratara.Abstractions.Security;
using Stratara.Shared.EventSourcing;

namespace Stratara.Infrastructure.Security.Serialization;

/// <summary>
/// <see cref="ISecureJsonSerializer"/> implementation that serializes objects to JSON, encrypting
/// either the whole payload (when the type carries <see cref="EncryptDataAttribute"/>) or only the
/// individual properties marked with <see cref="EncryptDataAttribute"/>.
/// </summary>
/// <remarks>
/// <para>
/// Encrypted segments are wrapped in <c>{"__enc": true, ...}</c> envelopes containing the algorithm,
/// nonce, tag, key id, and ciphertext. The serializer auto-detects encrypted segments on the read
/// path by looking for the <c>__enc</c> marker.
/// </para>
/// <para>
/// The additional-authenticated-data (AAD) for each encryption is <c>tenantId|userId|scope</c>
/// where <c>scope</c> is either the literal <c>"class"</c> for whole-object encryption or the
/// property name for per-property encryption — so a ciphertext copied between properties or
/// between subjects fails decryption.
/// </para>
/// <para>
/// A <see cref="FieldChangedEvent{TAggregate}"/> carries no marks of its own: its value is protected
/// when the property it names is marked on the aggregate, at that mark's level, with the scope
/// <c>field-changed:&lt;PropertyName&gt;</c> binding the value to the readable name.
/// </para>
/// </remarks>
internal sealed partial class SecureJsonSerializer(
    IKeyStore keyStore,
    IEncryptionFactory encryptionFactory,
    ILogger<SecureJsonSerializer>? logger = null,
    IHostEnvironment? environment = null) : ISecureJsonSerializer
{
    private const string RecordedPropertyName = nameof(FieldChangedEvent<object>.PropertyName);
    private const string RecordedNewValue = nameof(FieldChangedEvent<object>.NewValue);

    private static readonly ConcurrentDictionary<Type, byte[]> DefaultValueJson = new();

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">Thrown when <paramref name="obj"/> is not a reference-type instance.</exception>
    public async Task<string> SerializeAsync<T>(T obj, Guid? tenantId = null, Guid? userId = null, CancellationToken cancellationToken = default)
    {
        if (obj is not object serializeObject)
        {
            throw new ArgumentException($"Cannot serialize {typeof(T).FullName} as it is not an object.");
        }

        return await SerializeAsync(serializeObject, tenantId, userId, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> SerializeAsync(object obj, Guid? tenantId = null, Guid? userId = null, CancellationToken cancellationToken = default)
    {
        var type = obj.GetType();
        var metadata = MetadataCache.GetOrCreateMetadata(type);
        if (!metadata.RequiresEncryption)
        {
            return JsonSerializer.Serialize(obj);
        }

        if (metadata.RecordedAggregate is { } aggregate)
        {
            return await SerializeRecordedChangeAsync(obj, metadata, aggregate, tenantId, userId, cancellationToken);
        }

        if (metadata.ClassAttribute is not null)
        {
            return await EncryptWholeJsonAsync(obj, metadata.ClassAttribute.Level, tenantId, userId, cancellationToken);
        }

        if (metadata.EncryptedProperties.Length > 0)
        {
            return await EncryptPropertiesAsync(obj, metadata, tenantId, userId, cancellationToken);
        }

        return JsonSerializer.Serialize(obj);
    }

    /// <inheritdoc/>
    public async Task<T?> DeserializeAsync<T>(string json, Guid? tenantId = null, Guid? userId = null, CancellationToken cancellationToken = default)
    {
        var obj = await DeserializeAsync(json, typeof(T), tenantId, userId, cancellationToken);
        return obj is T tObj ? tObj : (T?)obj;
    }

    /// <inheritdoc/>
    public async Task<object?> DeserializeAsync(string json, Type returnType, Guid? tenantId = null, Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        var metadata = MetadataCache.GetOrCreateMetadata(returnType);

        // Before the marks are consulted: a value recorded as ciphertext stays readable after its
        // property, or every property of the aggregate, is no longer marked.
        if (metadata.RecordedAggregate is not null)
        {
            return await DeserializeRecordedChangeAsync(json, returnType, tenantId, userId, cancellationToken);
        }

        if (!metadata.RequiresEncryption)
        {
            return JsonSerializer.Deserialize(json, returnType);
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (IsEncryptedWrapper(root))
        {
            return await DecryptWholeJsonAsync(root, returnType, tenantId, userId, cancellationToken);
        }

        var contract = StoredContract.For(returnType, metadata);
        if (HasAnyEncryptedProperties(root) || (metadata.ClassAttribute is null && IsTheStoredForm(root, contract)))
        {
            return await DecryptPropertiesAsync(root, returnType, contract, tenantId, userId, cancellationToken);
        }

        return JsonSerializer.Deserialize(json, returnType);
    }

    private async Task<string> SerializeRecordedChangeAsync(object change, EncryptionMetadata metadata, EncryptionMetadata aggregate,
        Guid? tenantId, Guid? userId, CancellationToken cancellationToken)
    {
        var propertyName = ValueOf(change, metadata, RecordedPropertyName) as string;
        var newValue = ValueOf(change, metadata, RecordedNewValue);
        if (newValue is null || propertyName is null || !aggregate.EncryptedPropertiesByName.TryGetValue(propertyName, out var marked))
        {
            return JsonSerializer.Serialize(change);
        }

        // The property's own converter is left out on purpose: replay reads the value through the
        // declared type alone, as it does for a change to an unmarked property. A value of another shape
        // than the property, which replay also accepts, cannot be written as the declared type.
        var declaredType = marked.Property.PropertyType;
        var plainJsonUtf8 = JsonSerializer.SerializeToUtf8Bytes(newValue, declaredType.IsInstanceOfType(newValue) ? declaredType : newValue.GetType());
        var wrapper = await EncryptToWrapperAsync(plainJsonUtf8, marked.Attribute.Level, tenantId, userId,
            SecurityConstants.RecordedChangeScopePrefix + propertyName, cancellationToken);

        var buffer = new ArrayBufferWriter<byte>();
        await using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString(RecordedPropertyName, propertyName);
        writer.WritePropertyName(RecordedNewValue);
        JsonSerializer.Serialize(writer, wrapper);
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static object? ValueOf(object change, EncryptionMetadata metadata, string memberName) =>
        Array.Find(metadata.AllProperties, accessor => accessor.Name == memberName)?.GetValue(change);

    private async Task<object?> DeserializeRecordedChangeAsync(string json, Type returnType, Guid? tenantId, Guid? userId,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(RecordedNewValue, out var stored) || !IsEncryptedWrapper(stored))
        {
            return JsonSerializer.Deserialize(json, returnType);
        }

        // A payload that lost its name decrypts under an empty one, and so fails authentication.
        var propertyName = root.TryGetProperty(RecordedPropertyName, out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : string.Empty;
        var wrapper = JsonSerializer.Deserialize<EncryptedWrapper>(stored.GetRawText())!;
        var plain = await DecryptToBytesAsync(wrapper, tenantId, userId, SecurityConstants.RecordedChangeScopePrefix + propertyName, cancellationToken);

        var buffer = new ArrayBufferWriter<byte>();
        await using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString(RecordedPropertyName, propertyName);
        writer.WritePropertyName(RecordedNewValue);
        if (plain is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteRawValue(plain, true);
        }

        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
        return JsonSerializer.Deserialize(buffer.WrittenSpan, returnType);
    }

    private async Task<string> EncryptWholeJsonAsync(object obj, DataSensitivityLevel level, Guid? tenantId, Guid? userId,
        CancellationToken cancellationToken)
    {
        var plainJsonUtf8 = JsonSerializer.SerializeToUtf8Bytes(obj);
        var wrapper = await EncryptToWrapperAsync(plainJsonUtf8, level, tenantId, userId, SecurityConstants.ClassScope, cancellationToken);
        return JsonSerializer.Serialize(wrapper);
    }

    private async Task<string> EncryptPropertiesAsync<T>(T obj, EncryptionMetadata metadata, Guid? tenantId,
        Guid? userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var buffer = new ArrayBufferWriter<byte>();
        await using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        var contract = StoredContract.For(obj.GetType(), metadata);
        foreach (var accessor in metadata.AllProperties)
        {
            if (!contract.MembersByClrName.TryGetValue(accessor.Name, out var member) || !member.IsWritten)
            {
                continue;
            }

            var value = accessor.GetValue(obj);
            if (member.ShouldSerialize?.Invoke(obj, value) == false)
            {
                continue;
            }

            await WriteEncryptedOrPlainAsync(writer, member, value, metadata, tenantId, userId, cancellationToken);
        }

        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private async Task WriteEncryptedOrPlainAsync(Utf8JsonWriter writer, StoredMember member, object? value, EncryptionMetadata metadata,
        Guid? tenantId, Guid? userId, CancellationToken cancellationToken)
    {
        writer.WritePropertyName(member.ClrName);
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (!metadata.EncryptedPropertiesByName.TryGetValue(member.ClrName, out var marked))
        {
            JsonSerializer.Serialize(writer, value, member.PropertyType, member.Options);
            return;
        }

        var plainJsonUtf8 = JsonSerializer.SerializeToUtf8Bytes(value, member.PropertyType, member.Options);
        var wrapper = await EncryptToWrapperAsync(plainJsonUtf8, marked.Attribute.Level, tenantId, userId, member.ClrName, cancellationToken);
        JsonSerializer.Serialize(writer, wrapper);
    }

    private async Task<EncryptedWrapper> EncryptToWrapperAsync(ReadOnlyMemory<byte> plainJsonUtf8, DataSensitivityLevel level, Guid? tenantId, Guid? userId,
        string scope, CancellationToken cancellationToken)
    {
        EnsureScopeCanIsolate(level, tenantId, userId);

        var keyScope = new KeyScope(level, tenantId?.ToString(), userId?.ToString());
        var keyMaterial = await keyStore.GetOrCreateCurrentKeyAsync(keyScope, cancellationToken);
        var dataEncryptionKey = keyMaterial.Key.ToArray();
        try
        {
            var aad = BuildAdditionalAuthenticatedData(tenantId, userId, scope);
            return encryptionFactory
                .Encrypt(plainJsonUtf8.Span, dataEncryptionKey, aad)
                .MapToEncryptedWrapper(keyMaterial.KeyId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataEncryptionKey.AsSpan());
        }
    }

    private async Task<object?> DecryptWholeJsonAsync(JsonElement root, Type returnType, Guid? tenantId, Guid? userId, CancellationToken cancellationToken)
    {
        var wrapper = JsonSerializer.Deserialize<EncryptedWrapper>(root.GetRawText())!;
        var plainJsonUtf8 = await DecryptToBytesAsync(wrapper, tenantId, userId, SecurityConstants.ClassScope, cancellationToken);
        return plainJsonUtf8 is null ? null : JsonSerializer.Deserialize(plainJsonUtf8, returnType);
    }

    private async Task<object?> DecryptPropertiesAsync(JsonElement root, Type returnType, StoredContract contract, Guid? tenantId, Guid? userId,
        CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        await using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();

        foreach (var member in contract.MembersByClrName.Values)
        {
            await WriteDecryptedOrPlainAsync(writer, member, root, tenantId, userId, cancellationToken);
        }

        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
        return JsonSerializer.Deserialize(buffer.WrittenSpan, returnType);
    }

    private async Task WriteDecryptedOrPlainAsync(Utf8JsonWriter writer, StoredMember member, JsonElement root,
        Guid? tenantId, Guid? userId, CancellationToken cancellationToken)
    {
        writer.WritePropertyName(member.ContractName);

        if (!root.TryGetProperty(member.ClrName, out var propEl) || propEl.ValueKind == JsonValueKind.Null)
        {
            WriteAbsentValue(writer, member.PropertyType);
            return;
        }

        if (!IsEncryptedWrapper(propEl))
        {
            propEl.WriteTo(writer);
            return;
        }

        var wrapper = JsonSerializer.Deserialize<EncryptedWrapper>(propEl.GetRawText())!;
        var plain = await DecryptToBytesAsync(wrapper, tenantId, userId, member.ClrName, cancellationToken);
        if (plain is null)
        {
            WriteAbsentValue(writer, member.PropertyType);
            return;
        }

        writer.WriteRawValue(plain, true);
    }

    private static void WriteAbsentValue(Utf8JsonWriter writer, Type propertyType)
    {
        if (!propertyType.IsValueType || Nullable.GetUnderlyingType(propertyType) is not null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteRawValue(DefaultValueJson.GetOrAdd(propertyType, CreateDefaultValueJson), true);
    }

    private static byte[] CreateDefaultValueJson(Type valueType) =>
        JsonSerializer.SerializeToUtf8Bytes(RuntimeHelpers.GetUninitializedObject(valueType), valueType);

    private async Task<byte[]?> DecryptToBytesAsync(EncryptedWrapper wrapper, Guid? tenantId, Guid? userId, string scope,
        CancellationToken cancellationToken)
    {
        var rawDataEncryptionKey = await keyStore.GetDataEncryptionKeyAsync(wrapper.KeyId, cancellationToken);
        if (rawDataEncryptionKey is null)
        {
            return null;
        }

        var dataEncryptionKey = (byte[])rawDataEncryptionKey.Clone();
        try
        {
            var encryptedData = wrapper.MapToEncryptedData();
            var aad = BuildAdditionalAuthenticatedData(tenantId, userId, scope);
            return encryptionFactory.Decrypt(encryptedData, dataEncryptionKey, aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataEncryptionKey.AsSpan());
        }
    }

    // Writing only. Reading deliberately has no equivalent check: data already encrypted into a
    // degenerate scope must stay decryptable, or the guard would destroy access rather than protect it.
    private void EnsureScopeCanIsolate(DataSensitivityLevel level, Guid? tenantId, Guid? userId)
    {
        // A level that claims isolation must have at least one identifying dimension to isolate by.
        // UserScoped without a user but with a tenant is not this case: it resolves to
        // "UserScoped:<tenant>:", which still separates tenants and is a deliberate part of how an
        // event's payload is bound to its stream's owner. What is refused is the collapse to a single
        // system-wide key, where every subject shares one and erasing one erases all.
        var claimsIsolation = level is DataSensitivityLevel.TenantScoped or DataSensitivityLevel.UserScoped;
        var hasTenant = tenantId is not null && tenantId != Guid.Empty;
        var hasUser = userId is not null && userId != Guid.Empty;

        if (!claimsIsolation || hasTenant || hasUser)
        {
            return;
        }

        var missing = level == DataSensitivityLevel.TenantScoped ? "TenantId" : "TenantId and UserId";

        // No IHostEnvironment means no host — a bare ServiceCollection, a test harness, an embedded
        // composition. Unknown is treated as "not development", because the permissive branch is the
        // one that lets unisolated data be written, and it should need a positive statement.
        var environmentName = environment?.EnvironmentName ?? "unknown (no IHostEnvironment registered)";
        if (environment?.IsDevelopment() == true)
        {
            LogScopeCannotIsolate(logger ?? NullLogger<SecureJsonSerializer>.Instance, level.ToString(), missing, environmentName);
            return;
        }

        throw new InvalidOperationException(
            $"Cannot encrypt at {level} because {missing} is absent (environment '{environmentName}'). " +
            $"With no identifying dimension the scope collapses to a single system-wide key, so every subject " +
            $"shares one and erasing one would erase all of them — the isolation {level} names would not exist. " +
            $"Supply the {missing}, or, for data that genuinely has no tenant, use " +
            $"{nameof(DataSensitivityLevel)}.{nameof(DataSensitivityLevel.Confidential)}, which claims a single " +
            "system-wide key and no isolation.");
    }

    [LoggerMessage(
        EventId = LogEvents.KeyManagement.ScopeCannotIsolate,
        Level = LogLevel.Warning,
        Message = "Encrypting at {Level} with no {MissingDimension}: every value in this position shares one key, so erasing one subject would erase all of them. Allowed because the environment is {EnvironmentName}; outside development this fails. Use Confidential for data that has no tenant.")]
    private static partial void LogScopeCannotIsolate(ILogger logger, string level, string missingDimension, string environmentName);

    private static byte[] BuildAdditionalAuthenticatedData(Guid? tenantId, Guid? userId, string scope)
    {
        if (tenantId is null)
        {
            throw new InvalidOperationException(
                "Cannot encrypt with a null TenantId — the AAD would not be tenant-bound. " +
                "Set ISessionContextProvider.Current before invoking secure serialization, or pass an explicit tenantId.");
        }
        return Encoding.UTF8.GetBytes($"{tenantId}|{userId}|{scope}");
    }

    private static bool IsEncryptedWrapper(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(SecurityConstants.EncryptionMarker, out _);

    // Without ciphertext in it, the field-by-field form and ordinary JSON look alike; a renamed
    // property under its CLR name is what only the former carries. Ordinary JSON keeps going straight
    // to the serializer, so a property it lacks keeps its initializer as it always has.
    private static bool IsTheStoredForm(JsonElement root, StoredContract contract) =>
        root.ValueKind == JsonValueKind.Object
        && Array.Exists(contract.NamesOnlyTheStoredFormUses, name => root.TryGetProperty(name, out _));

    private static bool HasAnyEncryptedProperties(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Any(property => IsEncryptedWrapper(property.Value));
    }
}