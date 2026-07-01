namespace Stratara.Abstractions.Reflections;

/// <summary>
/// Normalizes assembly-qualified type names to their version-independent form so a name persisted by a
/// previous package build (<c>..., Version=1.2.3.4, Culture=neutral, PublicKeyToken=...</c>) still
/// matches after the producing assembly is upgraded. Shared by <see cref="TrustedTypeResolver"/> and the
/// event-upcasting layer so both compare persisted type names the same way.
/// </summary>
internal static class TypeNameNormalization
{
    /// <summary>
    /// Strips the assembly <c>Version</c> / <c>Culture</c> / <c>PublicKeyToken</c> segments from an
    /// assembly-qualified name, keeping the full type name and the simple assembly name
    /// (<c>"Namespace.Type, Assembly"</c>). A name without an assembly segment is returned unchanged.
    /// </summary>
    /// <param name="assemblyQualifiedName">The type name to normalize.</param>
    /// <returns>The version-independent form.</returns>
    public static string ToVersionIndependent(string assemblyQualifiedName)
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
