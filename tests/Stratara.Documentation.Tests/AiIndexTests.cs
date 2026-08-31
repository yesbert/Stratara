using System.Text.RegularExpressions;

namespace Stratara.Documentation.Tests;

/// <summary>
/// <c>llms.txt</c> tells an assistant to prefer its facts over anything the model already believes.
/// A stale fact there is therefore worse than a stale fact elsewhere — it is asserted as current.
/// <para>
/// The stated stable version is checked against the newest dated section of the changelog rather
/// than against <c>VersionPrefix</c>. The two used to be the same number, and stopped being so once
/// a version could be tagged as a prerelease: <c>VersionPrefix</c> then names the version being
/// worked toward, which stays unreleased for the whole cycle. Comparing against it would have
/// <c>llms.txt</c> advertise a version nobody can install, which is the exact failure this guards.
/// </para>
/// </summary>
public partial class AiIndexTests
{
    [GeneratedRegex(@"^##\s*\[(\d+\.\d+\.\d+)\][^\n]*?\d{4}-\d{2}-\d{2}", RegexOptions.Multiline)]
    private static partial Regex NewestReleasedVersionPattern();

    [GeneratedRegex(@"current stable version is\s*\r?\n?\*\*([0-9.]+)\*\*")]
    private static partial Regex StatedStableVersionPattern();

    [Fact]
    public void TheStatedStableVersion_MatchesTheNewestRelease()
    {
        var root = RepositoryRoot.Locate();
        var released = NewestReleasedVersionPattern()
            .Match(File.ReadAllText(Path.Combine(root, "CHANGELOG.md"))).Groups[1].Value;
        var stated = StatedStableVersionPattern()
            .Match(File.ReadAllText(Path.Combine(root, "llms.txt"))).Groups[1].Value;

        Assert.Equal(released, stated);
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
