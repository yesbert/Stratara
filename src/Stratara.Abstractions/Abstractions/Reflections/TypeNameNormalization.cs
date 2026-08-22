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
    /// (<c>"Namespace.Type, Assembly"</c>). A closed generic keeps its type arguments and each argument
    /// is reduced the same way, so <c>"Ns.Event`1[[Ns.Payload, Payloads, Version=1.0.0.0, ...]], Events,
    /// Version=2.0.0.0, ..."</c> becomes <c>"Ns.Event`1[[Ns.Payload, Payloads]], Events"</c>. A name
    /// without an assembly segment is returned unchanged.
    /// </summary>
    /// <param name="assemblyQualifiedName">The type name to normalize.</param>
    /// <returns>The version-independent form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assemblyQualifiedName"/> is <c>null</c>.</exception>
    public static string ToVersionIndependent(string assemblyQualifiedName)
    {
        ArgumentNullException.ThrowIfNull(assemblyQualifiedName);

        var separator = IndexOfTopLevelComma(assemblyQualifiedName);
        if (separator < 0)
        {
            return NormalizeTypeArguments(assemblyQualifiedName).Trim();
        }

        var typeName = NormalizeTypeArguments(assemblyQualifiedName[..separator]).Trim();
        var assemblyName = SimpleAssemblyName(assemblyQualifiedName[(separator + 1)..]);
        return assemblyName.Length == 0 ? typeName : $"{typeName}, {assemblyName}";
    }

    private static int IndexOfTopLevelComma(string value)
    {
        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    return i;
            }
        }

        return -1;
    }

    private static string SimpleAssemblyName(string assemblySegment)
    {
        var end = assemblySegment.IndexOf(',');
        return (end < 0 ? assemblySegment : assemblySegment[..end]).Trim();
    }

    private static string NormalizeTypeArguments(string typeName)
    {
        var open = typeName.IndexOf("[[", StringComparison.Ordinal);
        if (open < 0)
        {
            return typeName;
        }

        var close = IndexOfMatchingBracket(typeName, open);
        if (close < 0)
        {
            return typeName;
        }

        var arguments = SplitTypeArguments(typeName, open + 1, close);
        return string.Concat(typeName[..(open + 1)], string.Join(',', arguments), typeName[close..]);
    }

    private static List<string> SplitTypeArguments(string typeName, int start, int end)
    {
        var arguments = new List<string>();
        var depth = 0;
        var segmentStart = start;

        for (var i = start; i < end; i++)
        {
            switch (typeName[i])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    arguments.Add(NormalizeTypeArgument(typeName[segmentStart..i]));
                    segmentStart = i + 1;
                    break;
            }
        }

        arguments.Add(NormalizeTypeArgument(typeName[segmentStart..end]));
        return arguments;
    }

    private static string NormalizeTypeArgument(string argument)
    {
        var trimmed = argument.Trim();
        return trimmed.StartsWith('[') && trimmed.EndsWith(']')
            ? $"[{ToVersionIndependent(trimmed[1..^1])}]"
            : trimmed;
    }

    private static int IndexOfMatchingBracket(string value, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }
}
