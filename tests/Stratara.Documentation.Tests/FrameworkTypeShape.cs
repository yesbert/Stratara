using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Stratara.Documentation.Tests;

/// <summary>
/// Compares a type a documentation fence declares against the framework type of the same name.
/// Compilation cannot do this: a fence that re-declares <c>IBusEnvelopeSigner</c> declares a new,
/// unrelated type, so a drifted declaration compiles perfectly while documenting something that
/// does not exist.
/// </summary>
public static class FrameworkTypeShape
{
    private static readonly Dictionary<string, string> KeywordAliases = new(StringComparer.Ordinal)
    {
        ["string"] = "String",
        ["bool"] = "Boolean",
        ["int"] = "Int32",
        ["long"] = "Int64",
        ["decimal"] = "Decimal",
        ["double"] = "Double",
        ["object"] = "Object",
        ["byte"] = "Byte",
        ["void"] = "Void",
    };

    private static readonly Lazy<IReadOnlyDictionary<string, Type>> FrameworkTypes = new(LoadFrameworkTypes);

    public static IReadOnlyList<string> Mismatches(DocumentationSnippet snippet)
    {
        ArgumentNullException.ThrowIfNull(snippet);

        var mismatches = new List<string>();
        var root = CSharpSyntaxTree.ParseText(snippet.Code, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();

        foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var name = declaration.Identifier.ValueText;
            if (!FrameworkTypes.Value.TryGetValue(name, out var frameworkType))
            {
                continue;
            }

            mismatches.AddRange(CompareMembers(declaration, frameworkType));
        }

        return mismatches;
    }

    /// <summary>
    /// Reports whether the fence re-declares a type the framework already has. Such a declaration is
    /// kept out of a page's compilation context: a later fence on the page means the framework's
    /// type, not the page's illustration of it.
    /// </summary>
    public static bool ReDeclaresAFrameworkType(string code) =>
        CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.Preview))
            .GetRoot()
            .DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Any(declaration => FrameworkTypes.Value.ContainsKey(declaration.Identifier.ValueText));

    private static IEnumerable<string> CompareMembers(TypeDeclarationSyntax declaration, Type frameworkType)
    {
        var documented = declaration.Members
            .OfType<MethodDeclarationSyntax>()
            .Select(method => Signature(
                method.Identifier.ValueText,
                Normalize(method.ReturnType.ToString()),
                method.ParameterList.Parameters.Select(p => Normalize(p.Type?.ToString() ?? "?"))))
            .ToList();

        if (documented.Count == 0)
        {
            yield break;
        }

        var actual = frameworkType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => Signature(
                method.Name,
                Normalize(method.ReturnType.Name),
                method.GetParameters().Select(p => Normalize(p.ParameterType.Name))))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var signature in documented.Where(s => !actual.Contains(s)))
        {
            yield return
                $"'{declaration.Identifier.ValueText}' documents '{signature}', which {frameworkType.FullName} does not declare. "
                + $"It declares: {string.Join(", ", actual.OrderBy(s => s, StringComparer.Ordinal))}.";
        }
    }

    private static string Signature(string name, string returnType, IEnumerable<string> parameterTypes) =>
        $"{returnType} {name}({string.Join(", ", parameterTypes)})";

    private static string Normalize(string typeName)
    {
        var trimmed = typeName.Trim().TrimEnd('?').Trim();
        var generic = trimmed.IndexOf('<', StringComparison.Ordinal);
        if (generic > 0)
        {
            trimmed = trimmed[..generic];
        }

        var lastDot = trimmed.LastIndexOf('.');
        if (lastDot >= 0)
        {
            trimmed = trimmed[(lastDot + 1)..];
        }

        var backtick = trimmed.IndexOf('`', StringComparison.Ordinal);
        if (backtick > 0)
        {
            trimmed = trimmed[..backtick];
        }

        return KeywordAliases.TryGetValue(trimmed, out var alias) ? alias : trimmed;
    }

    private static IReadOnlyDictionary<string, Type> LoadFrameworkTypes()
    {
        var types = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "Stratara.*.dll"))
        {
            if (Path.GetFileNameWithoutExtension(path).EndsWith(".Tests", StringComparison.Ordinal))
            {
                continue;
            }

            IEnumerable<Type> exported;
            try
            {
                exported = Assembly.LoadFrom(path).GetExportedTypes();
            }
            catch (FileLoadException)
            {
                continue;
            }

            foreach (var type in exported)
            {
                types.TryAdd(type.Name.Split('`')[0], type);
            }
        }

        return types;
    }
}
