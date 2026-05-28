using System.Collections.Concurrent;
using System.Reflection;
using Stratara.Infrastructure.Security.Cryptography;
using Stratara.Shared.Reflections;
using Stratara.Abstractions.Security;

namespace Stratara.Infrastructure.Security.Serialization;

internal static class MetadataCache
{
    private static readonly ConcurrentDictionary<Type, EncryptionMetadata> Cache = new();

    public static EncryptionMetadata GetOrCreateMetadata(Type type)
    {
        return Cache.GetOrAdd(type, t =>
        {
            var classAttr = t.GetCustomAttribute<EncryptDataAttribute>();
            var properties = t.GetProperties(BindingFlags.Instance | BindingFlags.Public);

            var propertyAccessors = new PropertyAccessor[properties.Length];
            var encryptedProps = new List<EncryptedPropertyAccessor>();

            var constructorParams = GetPrimaryConstructorParameters(t);

            for (var i = 0; i < properties.Length; i++)
            {
                var prop = properties[i];
                var accessor = new PropertyAccessor(prop, t);
                propertyAccessors[i] = accessor;

                var attr = prop.GetCustomAttribute<EncryptDataAttribute>();
                if (attr is null && constructorParams.TryGetValue(prop.Name, out var param))
                {
                    attr = param.GetCustomAttribute<EncryptDataAttribute>();
                }

                if (attr is not null)
                {
                    encryptedProps.Add(new EncryptedPropertyAccessor(accessor, attr));
                }
            }

            return new EncryptionMetadata(classAttr, propertyAccessors, encryptedProps.ToArray());
        });
    }

    private static Dictionary<string, ParameterInfo> GetPrimaryConstructorParameters(Type type)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var primaryConstructor = constructors
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (primaryConstructor is null)
        {
            return [];
        }

        return primaryConstructor
            .GetParameters()
            .ToDictionary(
                p => p.Name ?? string.Empty,
                p => p,
                StringComparer.OrdinalIgnoreCase
            );
    }
}