using System.Text.RegularExpressions;

namespace Stratara.Documentation.Tests;

/// <summary>
/// <c>llms.txt</c> tells an assistant to prefer its facts over anything the model already believes.
/// A stale fact there is therefore worse than a stale fact elsewhere — it is asserted as current.
/// </summary>
public partial class AiIndexTests
{
    [GeneratedRegex(@"<VersionPrefix>([^<]+)</VersionPrefix>")]
    private static partial Regex VersionPrefixPattern();

    [GeneratedRegex(@"current stable version is\s*\r?\n?\*\*([0-9.]+)\*\*")]
    private static partial Regex StatedStableVersionPattern();

    [Fact]
    public void TheStatedStableVersion_MatchesTheLockstepVersion()
    {
        var root = RepositoryRoot.Locate();
        var version = VersionPrefixPattern()
            .Match(File.ReadAllText(Path.Combine(root, "Directory.Build.props"))).Groups[1].Value;
        var stated = StatedStableVersionPattern()
            .Match(File.ReadAllText(Path.Combine(root, "llms.txt"))).Groups[1].Value;

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
