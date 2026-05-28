using Stratara.Infrastructure.Security.Cryptography;
using Stratara.Shared.Reflections;
using Stratara.Abstractions.Security;

namespace Stratara.Infrastructure.Security.Serialization;

internal sealed class EncryptionMetadata(
    EncryptDataAttribute? classAttribute,
    PropertyAccessor[] allProperties,
    EncryptedPropertyAccessor[] encryptedProperties)
{
    public EncryptDataAttribute? ClassAttribute { get; } = classAttribute;

    public PropertyAccessor[] AllProperties { get; } = allProperties;

    public EncryptedPropertyAccessor[] EncryptedProperties { get; } = encryptedProperties;

    public IReadOnlyDictionary<string, EncryptDataAttribute> EncryptedAttributesByName { get; } = CreateMap(encryptedProperties);

    public bool RequiresEncryption { get; } = classAttribute is not null || encryptedProperties.Length > 0;

    private static Dictionary<string, EncryptDataAttribute> CreateMap(EncryptedPropertyAccessor[] items)
    {
        var dict = new Dictionary<string, EncryptDataAttribute>(items.Length, StringComparer.Ordinal);
        foreach (var item in items)
        {
            dict[item.Property.Name] = item.Attribute;
        }

        return dict;
    }
}