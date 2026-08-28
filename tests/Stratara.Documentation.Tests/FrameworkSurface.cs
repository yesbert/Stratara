using System.Reflection;

namespace Stratara.Documentation.Tests;

/// <summary>
/// Reflection over the published assemblies. Every assertion in this project resolves its facts
/// from the types that ship, never from the source text — a check that reads source drifts the same
/// way the documentation drifts.
/// </summary>
public static class FrameworkSurface
{
    private static readonly Lazy<IReadOnlyList<Assembly>> Assemblies = new(Load);

    public static IReadOnlyList<Assembly> Published => Assemblies.Value;

    public static IEnumerable<Type> ExportedTypes =>
        Assemblies.Value.SelectMany(assembly => assembly.GetExportedTypes());

    public static IEnumerable<(Type Type, string SectionName)> OptionsWithSectionName()
    {
        foreach (var type in ExportedTypes)
        {
            var field = type.GetField("SectionName", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (field?.IsLiteral == true && field.GetRawConstantValue() is string value && value.Length > 0)
            {
                yield return (type, value);
            }
        }
    }

    private static IReadOnlyList<Assembly> Load()
    {
        var assemblies = new List<Assembly>();

        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "Stratara.*.dll").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (Path.GetFileNameWithoutExtension(path).EndsWith(".Tests", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                assemblies.Add(Assembly.LoadFrom(path));
            }
            catch (FileLoadException)
            {
            }
            catch (BadImageFormatException)
            {
            }
        }

        return assemblies;
    }
}
