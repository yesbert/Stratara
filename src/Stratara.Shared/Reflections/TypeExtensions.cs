using System.Collections.Concurrent;

namespace Stratara.Shared.Reflections;

/// <summary>
/// Extension helpers that produce version-independent CLR type-name strings suitable for embedding
/// in persisted events and the outbox. Assembly version / culture / public-key-token segments are
/// trimmed so that an assembly rev-bump does not invalidate previously-stored type names.
/// </summary>
public static class TypeExtensions
{
    private static readonly ConcurrentDictionary<Type, string> s_qualifiedNameCache = new();

    /// <summary>
    /// Returns a cached, version-independent qualified type name for <paramref name="type"/>.
    /// Falls back to <see cref="Type.FullName"/> or the unqualified <c>Type.Name</c> when
    /// <see cref="Type.AssemblyQualifiedName"/> is unavailable.
    /// </summary>
    /// <param name="type">The type to name.</param>
    /// <returns>A stable type-name string safe to persist.</returns>
    public static string GetQualifiedTypeName(this Type type) =>
        s_qualifiedNameCache.GetOrAdd(type, t => t.AssemblyQualifiedName ?? t.FullName ?? t.Name);

    /// <summary>
    /// Strips assembly version / culture / public-key-token segments from a fully-qualified
    /// assembly-qualified type name, retaining only <c>TypeName, AssemblyName</c>.
    /// </summary>
    /// <param name="assemblyQualifiedName">A fully-qualified assembly-qualified type-name string.</param>
    /// <returns>The version-independent equivalent suitable for <see cref="Type.GetType(string)"/> lookups across assembly revisions.</returns>
    public static string GetVersionIndependentTypeName(this string assemblyQualifiedName)
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
