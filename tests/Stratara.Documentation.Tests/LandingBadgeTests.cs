using System.Text.RegularExpressions;

namespace Stratara.Documentation.Tests;

/// <summary>
/// The documentation site serves its badges from <c>docs/assets/badges/</c> rather than from a
/// badge service, so that a visitor's browser makes no third-party request before that visitor has
/// agreed to anything. That property is only worth having if it holds for every page, and a served
/// badge only stays true if something notices when it stops being.
/// <para>
/// The release badge is checked against the newest dated section of the changelog for the same
/// reason <c>llms.txt</c> is: <c>VersionPrefix</c> names the version being worked toward, which
/// stays unreleased for the whole cycle, so comparing against it would advertise a version nobody
/// can install.
/// </para>
/// </summary>
public partial class LandingBadgeTests
{
    [GeneratedRegex(@"^##\s*\[(\d+\.\d+\.\d+)\][^\n]*?\d{4}-\d{2}-\d{2}", RegexOptions.Multiline)]
    private static partial Regex NewestReleasedVersionPattern();

    [GeneratedRegex(@"v(\d+\.\d+\.\d+)")]
    private static partial Regex BadgeVersionPattern();

    // Quoted, single-quoted, bare and protocol-relative all reach another host, and so does a
    // Markdown image. A guard that only knew the shape we happen to write today would pass the
    // first time somebody wrote it differently, which is the one moment it exists for.
    [GeneratedRegex("""<img[^>]+src\s*=\s*["']?(?:https?:)?//|!\[[^\]]*\]\(\s*(?:https?:)?//""")]
    private static partial Regex ExternallyHostedImagePattern();

    [Fact]
    public void TheReleaseBadge_NamesTheNewestRelease()
    {
        var root = RepositoryRoot.Locate();
        var release = NewestReleasedVersionPattern()
            .Match(File.ReadAllText(Path.Combine(root, "CHANGELOG.md")));
        var badge = BadgeVersionPattern()
            .Match(File.ReadAllText(Path.Combine(root, "docs", "assets", "badges", "nuget.svg")));

        // Without these, a changelog heading or badge format we stopped recognising would leave
        // both sides empty and the comparison would agree that nothing is wrong.
        Assert.True(release.Success, "No dated release section was found in CHANGELOG.md.");
        Assert.True(badge.Success, "No version was found in docs/assets/badges/nuget.svg.");
        Assert.Equal(release.Groups[1].Value, badge.Groups[1].Value);
    }

    [Fact]
    public void NoDocumentationPage_LoadsAnImageFromAnotherHost()
    {
        var offenders = DocumentationFiles.Enumerate()
            .Where(file => ExternallyHostedImagePattern().IsMatch(File.ReadAllText(file)))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These pages load an image from another host, which reaches that host before the "
            + $"visitor has consented to anything. Serve the image from docs/assets/ instead: "
            + $"{string.Join(", ", offenders)}");
    }
}
