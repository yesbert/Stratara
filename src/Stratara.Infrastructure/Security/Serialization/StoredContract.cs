using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Stratara.Infrastructure.Security.Serialization;

/// <summary>
/// What the field-by-field form stores for a type, read from the serializer's own contract for it:
/// which properties it would write, under which name it expects each back, and through which converter.
/// The stored form keeps the CLR property names — rows already written use them, and an event
/// upcaster works on them — so only the mapping onto the contract lives here.
/// </summary>
internal sealed class StoredContract
{
    private static readonly ConcurrentDictionary<Type, StoredContract> Cache = new();

    private StoredContract(Dictionary<string, StoredMember> membersByClrName)
    {
        MembersByClrName = membersByClrName;
        var contractNames = membersByClrName.Values.Select(member => member.ContractName).ToHashSet(StringComparer.Ordinal);
        NamesOnlyTheStoredFormUses = membersByClrName.Values
            .Where(member => member.ClrName != member.ContractName && !contractNames.Contains(member.ClrName))
            .Select(member => member.ClrName)
            .ToArray();
    }

    public IReadOnlyDictionary<string, StoredMember> MembersByClrName { get; }

    /// <summary>
    /// CLR names of renamed properties that no property of the type uses as its contract name, so
    /// finding one in a payload tells the stored form apart from ordinary JSON.
    /// </summary>
    public string[] NamesOnlyTheStoredFormUses { get; }

    public static StoredContract For(Type type, EncryptionMetadata metadata) =>
        Cache.GetOrAdd(type, static (t, m) => Create(t, m), metadata);

    private static StoredContract Create(Type type, EncryptionMetadata metadata)
    {
        var typeInfo = JsonSerializerOptions.Default.GetTypeInfo(type);

        // A type that is not serialized as an object by its contract — a type-level converter, say —
        // has no per-property contract to follow, so its properties are stored as they always were.
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return new StoredContract(metadata.AllProperties.ToDictionary(
                accessor => accessor.Name,
                accessor => new StoredMember(accessor.Name, accessor.Name, accessor.PropertyType, IsWritten: true, null, null),
                StringComparer.Ordinal));
        }

        var publicNames = metadata.AllProperties.Select(accessor => accessor.Name).ToHashSet(StringComparer.Ordinal);
        var members = new Dictionary<string, StoredMember>(StringComparer.Ordinal);
        foreach (var property in typeInfo.Properties)
        {
            if (property.AttributeProvider is not PropertyInfo clrProperty || !publicNames.Contains(clrProperty.Name))
            {
                continue;
            }

            members[clrProperty.Name] = new StoredMember(
                clrProperty.Name,
                property.Name,
                property.PropertyType,
                IsWritten: property.Get is not null,
                property.ShouldSerialize,
                property.CustomConverter is { } converter ? new JsonSerializerOptions { Converters = { converter } } : null);
        }

        return new StoredContract(members);
    }
}

/// <summary>One property as the field-by-field form stores it.</summary>
/// <param name="ClrName">The name the stored form writes the property under.</param>
/// <param name="ContractName">The name the serializer's contract reads the property from.</param>
/// <param name="PropertyType">The property's declared type.</param>
/// <param name="IsWritten">Whether the serializer writes the property at all.</param>
/// <param name="ShouldSerialize">The contract's condition for writing a value, where it has one.</param>
/// <param name="Options">Options carrying the property's own converter, or <see langword="null"/> for the default.</param>
internal sealed record StoredMember(
    string ClrName,
    string ContractName,
    Type PropertyType,
    bool IsWritten,
    Func<object, object?, bool>? ShouldSerialize,
    JsonSerializerOptions? Options);
