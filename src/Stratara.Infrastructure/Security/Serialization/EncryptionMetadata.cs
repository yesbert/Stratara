using Stratara.Infrastructure.Security.Cryptography;
using Stratara.Shared.Reflections;
using Stratara.Abstractions.Security;

namespace Stratara.Infrastructure.Security.Serialization;

internal sealed class EncryptionMetadata(
    EncryptDataAttribute? classAttribute,
    PropertyAccessor[] allProperties,
    EncryptedPropertyAccessor[] encryptedProperties,
    EncryptionMetadata? recordedAggregate = null)
{
    public EncryptDataAttribute? ClassAttribute { get; } = classAttribute;

    public PropertyAccessor[] AllProperties { get; } = allProperties;

    public EncryptedPropertyAccessor[] EncryptedProperties { get; } = encryptedProperties;

    public IReadOnlyDictionary<string, EncryptedPropertyAccessor> EncryptedPropertiesByName { get; } = CreateMap(encryptedProperties);

    /// <summary>
    /// For a recorded field change, the metadata of the aggregate it records a change of: the marks that
    /// protect its value are the aggregate's, which the event type itself cannot carry.
    /// </summary>
    public EncryptionMetadata? RecordedAggregate { get; } = recordedAggregate;

    public bool RequiresEncryption { get; } =
        classAttribute is not null || encryptedProperties.Length > 0 || recordedAggregate is { EncryptedProperties.Length: > 0 };

    private static Dictionary<string, EncryptedPropertyAccessor> CreateMap(EncryptedPropertyAccessor[] items)
    {
        var dict = new Dictionary<string, EncryptedPropertyAccessor>(items.Length, StringComparer.Ordinal);
        foreach (var item in items)
        {
            dict[item.Property.Name] = item;
        }

        return dict;
    }
}