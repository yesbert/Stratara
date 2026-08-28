namespace Stratara.Documentation.Tests;

/// <summary>
/// Resolves the repository root from the test binary's location, so tests can read the
/// documentation sources rather than a copy of them.
/// </summary>
public static class RepositoryRoot
{
    private const string RootMarker = "Directory.Build.props";

    public static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RootMarker))
                && Directory.Exists(Path.Combine(directory.FullName, "docs"))
                && Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"No repository root found above '{AppContext.BaseDirectory}' — looked for a directory containing '{RootMarker}', 'docs' and 'src'.");
    }
}
