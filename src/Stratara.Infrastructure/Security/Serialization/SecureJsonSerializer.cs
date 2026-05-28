using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stratara.Infrastructure.Security.Mapping;
using Stratara.Abstractions.Security;
using Stratara.Shared.Reflections;

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
/// </remarks>
internal sealed class SecureJsonSerializer(IKeyStore keyStore, IEncryptionFactory encryptionFactory) : ISecureJsonSerializer
{
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

        if (HasAnyEncryptedProperties(root))
        {
            return await DecryptPropertiesAsync(root, returnType, metadata, tenantId, userId, cancellationToken);
        }

        return JsonSerializer.Deserialize(json, returnType);
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
        foreach (var accessor in metadata.AllProperties)
        {
            var value = accessor.GetValue(obj);
            await WriteEncryptedOrPlainAsync(writer, accessor, value, metadata, tenantId, userId, cancellationToken);
        }

        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private async Task WriteEncryptedOrPlainAsync(Utf8JsonWriter writer, PropertyAccessor accessor, object? value, EncryptionMetadata metadata,
        Guid? tenantId, Guid? userId, CancellationToken cancellationToken)
    {
        writer.WritePropertyName(accessor.Name);
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (!metadata.EncryptedAttributesByName.TryGetValue(accessor.Name, out var attr))
        {
            JsonSerializer.Serialize(writer, value, accessor.PropertyType);
            return;
        }

        var plainJsonUtf8 = JsonSerializer.SerializeToUtf8Bytes(value, accessor.PropertyType);
        var wrapper = await EncryptToWrapperAsync(plainJsonUtf8, attr.Level, tenantId, userId, accessor.Name, cancellationToken);
        JsonSerializer.Serialize(writer, wrapper);
    }

    private async Task<EncryptedWrapper> EncryptToWrapperAsync(ReadOnlyMemory<byte> plainJsonUtf8, DataSensitivityLevel level, Guid? tenantId, Guid? userId,
        string scope, CancellationToken cancellationToken)
    {
        var keyId = await keyStore.EnsureKeyAsync(level, tenantId, userId, cancellationToken);
        var rawDataEncryptionKey = await keyStore.GetDataEncryptionKeyAsync(keyId, cancellationToken)
                                   ?? throw new InvalidOperationException("Key revoked or missing.");
        var dataEncryptionKey = (byte[])rawDataEncryptionKey.Clone();
        try
        {
            var aad = BuildAdditionalAuthenticatedData(tenantId, userId, scope);
            return encryptionFactory
                .Encrypt(plainJsonUtf8.Span, dataEncryptionKey, aad)
                .MapToEncryptedWrapper(keyId);
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

    private async Task<object?> DecryptPropertiesAsync(JsonElement root, Type returnType, EncryptionMetadata metadata, Guid? tenantId, Guid? userId,
        CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        await using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();

        foreach (var accessor in metadata.AllProperties)
        {
            await WriteDecryptedOrPlainAsync(writer, accessor, root, tenantId, userId, cancellationToken);
        }

        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
        return JsonSerializer.Deserialize(buffer.WrittenSpan, returnType);
    }

    private async Task WriteDecryptedOrPlainAsync(Utf8JsonWriter writer, PropertyAccessor accessor, JsonElement root,
        Guid? tenantId, Guid? userId, CancellationToken cancellationToken)
    {
        writer.WritePropertyName(accessor.Name);

        if (!root.TryGetProperty(accessor.Name, out var propEl) || propEl.ValueKind == JsonValueKind.Null)
        {
            writer.WriteNullValue();
            return;
        }

        if (!IsEncryptedWrapper(propEl))
        {
            propEl.WriteTo(writer);
            return;
        }

        var wrapper = JsonSerializer.Deserialize<EncryptedWrapper>(propEl.GetRawText())!;
        var plain = await DecryptToBytesAsync(wrapper, tenantId, userId, accessor.Name, cancellationToken);
        if (plain is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteRawValue(plain, true);
    }

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

    private static bool HasAnyEncryptedProperties(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Any(property => IsEncryptedWrapper(property.Value));
    }
}