using System.Text.Json;

namespace Stratara.Documentation.Tests;

/// <summary>
/// The publish and SonarQube pipelines build one thing — <c>Stratara.Publish.slnf</c> — and then run
/// every project matching <c>tests/*/*.Tests.csproj</c> with <c>--no-build</c>. Those are two
/// independent facts about a test project, and nothing reconciled them: a project that matched the
/// glob but was missing from the filter had no build output to run, and the pipelines failed on it.
/// That is how <c>Stratara.Documentation.Tests</c> itself broke both pipelines.
///
/// This guard runs in the unit-test pipeline, which builds each test project directly rather than
/// through the filter — so it still fires for the project that is missing from the filter.
/// </summary>
public class PublishFilterCoverageTests
{
    private const string Filter = "Stratara.Publish.slnf";

    [Fact]
    public void EveryTestProjectThePipelinesRun_IsInThePublishFilter()
    {
        var missing = DiscoverTestProjects().Except(ReadFilteredProjects(), StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0,
            $"These test projects match the pipelines' glob but are absent from {Filter}, so the "
            + $"pipelines never build them and `dotnet test --no-build` fails on each: {string.Join(", ", missing)}.");
    }

    /// <summary>Mirrors the <c>tests/*/*.Tests.csproj</c> glob the pipeline scripts iterate over.</summary>
    private static IEnumerable<string> DiscoverTestProjects()
    {
        var root = RepositoryRoot.Locate();

        return Directory
            .EnumerateFiles(Path.Combine(root, "tests"), "*.Tests.csproj", SearchOption.AllDirectories)
            .Where(path => Path.GetDirectoryName(Path.GetDirectoryName(path)) == Path.Combine(root, "tests"))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    private static IEnumerable<string> ReadFilteredProjects()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot.Locate(), Filter)));

        return [.. document.RootElement
            .GetProperty("solution")
            .GetProperty("projects")
            .EnumerateArray()
            .Select(project => project.GetString()!.Replace('\\', '/'))];
    }
}
