using System.Reflection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Domain;
using Stratara.Abstractions.EventSourcing;
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
/// in production. It also refuses a registered aggregate, or an event payload type a registered
/// aggregate applies, that is marked for encryption as a whole: once its subject is erased nothing of
/// it can be read, and the replay that meets it stops there.
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

        var markedAsAWhole = typeResolver.RegisteredTypes
            .Where(IsAggregate)
            .SelectMany(aggregate => AppliedEventTypes(aggregate).Prepend(aggregate))
            .Distinct()
            .Where(WholeTypeStreamEncryption.IsMarkedAsAWhole)
            .ToList();
        if (markedAsAWhole.Count > 0)
        {
            throw WholeTypeStreamEncryption.Refusal(markedAsAWhole);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool IsAggregate(Type type) =>
        typeof(IAggregate).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false };

    private static IEnumerable<Type> AppliedEventTypes(Type aggregateType) =>
        aggregateType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == "Apply" && !method.IsGenericMethodDefinition)
            .Select(method => method.GetParameters())
            .Where(parameters => parameters.Length == 1)
            .Select(parameters => PayloadTypeOf(parameters[0].ParameterType));

    private static Type PayloadTypeOf(Type parameterType) =>
        parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(IEvent<>)
            ? parameterType.GetGenericArguments()[0]
            : parameterType;

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
