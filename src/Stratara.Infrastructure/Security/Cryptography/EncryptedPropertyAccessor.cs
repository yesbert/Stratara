using Stratara.Shared.Reflections;
using Stratara.Abstractions.Security;

namespace Stratara.Infrastructure.Security.Cryptography;

internal sealed class EncryptedPropertyAccessor(PropertyAccessor property, EncryptDataAttribute attribute)
{
    public PropertyAccessor Property { get; } = property;

    public EncryptDataAttribute Attribute { get; } = attribute;
}