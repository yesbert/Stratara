using System.Reflection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;

namespace Stratara.Infrastructure.Security.Serialization;

/// <summary>
/// Hosted service that walks the <see cref="ITrustedTypeResolver"/> allowlist at host start and
/// asserts that <see cref="EncryptionMetadata.RequiresEncryption"/> agrees with whether the type
/// actually carries any <see cref="EncryptDataAttribute"/>. Defence-in-depth for
/// <see cref="SecureJsonSerializer"/>: if a future contributor adds a new attribute branch (e.g.
/// tenant-conditional encryption) without updating <c>RequiresEncryption</c>, instances of that
/// type would silently serialise unencrypted. This guard surfaces the drift at startup instead of
/// in production.
/// </summary>
internal sealed class EncryptionMetadataDriftGuard(ITrustedTypeResolver typeResolver) : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var type in typeResolver.RegisteredTypes)
        {
            var metadata = MetadataCache.GetOrCreateMetadata(type);
            var hasAnyAttribute = HasAnyEncryptDataAttribute(type);
            if (metadata.RequiresEncryption == hasAnyAttribute)
            {
                continue;
            }

            throw new InvalidOperationException(
                $"EncryptionMetadata drift on '{type.FullName}': RequiresEncryption={metadata.RequiresEncryption} " +
                $"but HasAnyEncryptDataAttribute={hasAnyAttribute}. SecureJsonSerializer would " +
                $"{(metadata.RequiresEncryption ? "encrypt" : "skip encryption for")} instances of this type — " +
                "verify EncryptionMetadata.RequiresEncryption is in sync with every [EncryptData] branch " +
                "(class attribute, property attribute, primary-constructor parameter attribute).");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool HasAnyEncryptDataAttribute(Type type)
    {
        if (type.GetCustomAttribute<EncryptDataAttribute>() is not null)
        {
            return true;
        }

        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        if (properties.Any(p => p.GetCustomAttribute<EncryptDataAttribute>() is not null))
        {
            return true;
        }

        var primaryCtor = type
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        return primaryCtor is not null
            && primaryCtor.GetParameters().Any(p => p.GetCustomAttribute<EncryptDataAttribute>() is not null);
    }
}
