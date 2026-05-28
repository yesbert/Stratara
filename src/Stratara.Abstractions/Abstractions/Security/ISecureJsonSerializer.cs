namespace Stratara.Abstractions.Security;

/// <summary>
/// JSON serializer that automatically encrypts properties marked with
/// <c>EncryptDataAttribute</c> using a key resolved from <see cref="IKeyStore"/> for
/// the supplied tenant + user scope.
/// </summary>
public interface ISecureJsonSerializer
{
    /// <summary>Serialise <paramref name="obj"/> to JSON, encrypting properties marked for the given scope.</summary>
    Task<string> SerializeAsync<T>(T obj, Guid? tenantId = null, Guid? userId = null, CancellationToken cancellationToken = default);

    /// <summary>Reflection-based variant of <see cref="SerializeAsync{T}"/>.</summary>
    Task<string> SerializeAsync(object obj, Guid? tenantId = null, Guid? userId = null, CancellationToken cancellationToken = default);

    /// <summary>Deserialise <paramref name="json"/>, decrypting any encrypted properties for the given scope.</summary>
    Task<T?> DeserializeAsync<T>(string json, Guid? tenantId = null, Guid? userId = null, CancellationToken cancellationToken = default);

    /// <summary>Reflection-based variant of <see cref="DeserializeAsync{T}"/>.</summary>
    Task<object?> DeserializeAsync(string json, Type returnType, Guid? tenantId = null, Guid? userId = null, CancellationToken cancellationToken = default);
}
