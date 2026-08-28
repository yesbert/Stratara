namespace Stratara.Documentation.Tests;

/// <summary>
/// Enumerates the hand-written documentation sources. The generated DocFX site and the generated
/// API reference are excluded: they are output, and verifying them would verify the generator.
/// </summary>
public static class DocumentationFiles
{
    private static readonly string[] ExcludedSegments =
    [
        Path.Combine("docs", "_site"),
        Path.Combine("docs", "reference", "api"),
    ];

    public static IReadOnlyList<string> Enumerate()
    {
        var root = RepositoryRoot.Locate();
        var docs = Path.Combine(root, "docs");

        return [.. Directory
            .EnumerateFiles(docs, "*.md", SearchOption.AllDirectories)
            .Where(path => !ExcludedSegments.Any(segment => path.Contains(segment, StringComparison.Ordinal)))
            .OrderBy(path => path, StringComparer.Ordinal)];
    }

    public static string RelativePath(string absolutePath) =>
        Path.GetRelativePath(RepositoryRoot.Locate(), absolutePath).Replace('\\', '/');
}
