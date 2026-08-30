using System.Text.RegularExpressions;

namespace Stratara.Documentation.Tests;

/// <summary>
/// <c>llms.txt</c> tells an assistant to prefer its facts over anything the model already believes.
/// A stale fact there is therefore worse than a stale fact elsewhere — it is asserted as current.
/// </summary>
public class AiIndexTests
{
    [Fact]
    public void TheStatedStableVersion_MatchesTheLockstepVersion()
    {
        var root = RepositoryRoot.Locate();
        var version = Regex.Match(
            File.ReadAllText(Path.Combine(root, "Directory.Build.props")),
            @"<VersionPrefix>([^<]+)</VersionPrefix>").Groups[1].Value;
        var stated = Regex.Match(
            File.ReadAllText(Path.Combine(root, "llms.txt")),
            @"current stable version is\s*\r?\n?\*\*([0-9.]+)\*\*").Groups[1].Value;

        Assert.Equal(version, stated);
    }

    [Fact]
    public void TheIndex_PointsAtTheGeneratedCatalogue()
    {
        var index = File.ReadAllText(Path.Combine(RepositoryRoot.Locate(), "llms.txt"));

        Assert.Contains("llms-full.txt", index, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCatalogueExistsAtTheRepositoryRoot() =>
        Assert.True(File.Exists(Path.Combine(RepositoryRoot.Locate(), "llms-full.txt")));
}
