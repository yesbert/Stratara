using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.Reflections;

/// <summary>
/// Default <see cref="ITrustedTypeResolver"/> backed by a process-wide <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// keyed by the version-independent type-name representation (assembly version / culture / public-key-token
/// segments stripped).
/// </summary>
/// <remarks>
/// Instances are intended to be registered as a singleton in the host's DI container. The DI scan extensions
/// populate the resolver during <c>ConfigureServices</c>; runtime lookups (from <c>MediatorCommandWorker</c>,
/// <c>EventMapperFactory</c>, <c>SnapshotService</c>) are lock-free reads.
/// </remarks>
public sealed class TrustedTypeResolver : ITrustedTypeResolver
{
    private readonly ConcurrentDictionary<string, Type> _types = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public Type Resolve(string typeName)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        var key = ToVersionIndependent(typeName);
        if (_types.TryGetValue(key, out var type))
        {
            return type;
        }
        throw new InvalidOperationException(
            $"Type '{typeName}' is not registered in the trusted-type resolver. " +
            "Register it via AddCommandHandlersFromAssemblyContaining<T>, AddProjectionsFromAssemblyContaining<T>, " +
            "AddSagasFromAssemblyContaining<T>, AddAggregatesFromAssemblyContaining<T>, or services.AddTrustedType<T>().");
    }

    /// <inheritdoc/>
    public bool TryResolve(string typeName, out Type? type)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        var key = ToVersionIndependent(typeName);
        return _types.TryGetValue(key, out type);
    }

    /// <inheritdoc/>
    public void Register(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var assemblyQualifiedName = type.AssemblyQualifiedName
            ?? throw new InvalidOperationException($"Cannot register type '{type.FullName}' — no AssemblyQualifiedName.");
        var key = ToVersionIndependent(assemblyQualifiedName);
        _types.TryAdd(key, type);
    }

    /// <inheritdoc/>
    [SuppressMessage("Major Code Smell", "S2365:Properties should not make collection or array copies",
        Justification = "Snapshot semantics are documented on ITrustedTypeResolver.RegisteredTypes; " +
                        "only consumer is the EncryptionMetadataDriftGuard at host start-up — not a hot path.")]
    public IReadOnlyCollection<Type> RegisteredTypes => _types.Values.ToArray();

    private static string ToVersionIndependent(string assemblyQualifiedName)
    {
        var commaIndex = assemblyQualifiedName.IndexOf(',');
        if (commaIndex < 0)
        {
            return assemblyQualifiedName;
        }

        var secondCommaIndex = assemblyQualifiedName.IndexOf(',', commaIndex + 1);
        return secondCommaIndex < 0
            ? assemblyQualifiedName
            : assemblyQualifiedName[..secondCommaIndex].Trim();
    }
}
