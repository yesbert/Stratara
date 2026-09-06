using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Stratara.Documentation.Tests;

/// <summary>
/// One image serves two places: <c>og:image</c> on the documentation site, and the repository's
/// social preview. It carries the site's host in its own artwork, which is the part that rots
/// silently — on 2026-09-06 the card still read <c>docs.stratara.tech</c> hours after that host was
/// retired, and nothing noticed because an image cannot be grepped.
/// <para>
/// So the host is asserted against <c>_appBaseUrl</c> in <c>docs/docfx.json</c>, the same value the
/// canonical links are built from. The card and the site cannot disagree about where the site is.
/// </para>
/// </summary>
public partial class SocialCardTests
{
    private const string CardSource = "build/social-card/social-card.html";
    private const string RenderedCard = "docs/assets/social-card.png";

    [GeneratedRegex(@"<div class=""meta"">(?<line>.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex MetaLinePattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagPattern();

    [Fact]
    public void TheCardNamesTheHostTheSiteIsServedFrom()
    {
        var root = RepositoryRoot.Locate();

        using var docfx = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "docs", "docfx.json")));
        var baseUrl = docfx.RootElement
            .GetProperty("build").GetProperty("globalMetadata").GetProperty("_appBaseUrl")
            .GetString();
        var expected = new Uri(baseUrl!).Host;

        var card = File.ReadAllText(Path.Combine(root, CardSource));
        var meta = MetaLinePattern().Match(card);
        Assert.True(meta.Success, $"{CardSource} has no <div class=\"meta\"> line to check.");

        // "Fast • Secure • stratara.tech" once the span tags are gone. A contains-check would pass
        // on "docs.stratara.tech", which is exactly the failure this test exists for, so the last
        // segment is compared whole.
        var segments = TagPattern().Replace(meta.Groups["line"].Value, "•")
            .Split('•', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var host = segments[^1];

        Assert.True(
            host == expected,
            $"The social card advertises '{host}' but the site is served from '{expected}'. Fix the "
            + $"meta line in {CardSource} and re-render with ./scripts/refresh-social-card.sh.");
    }

    [Fact]
    public void TheRenderedCardIsPresentAtTheSizeGitHubAsksFor()
    {
        var png = Path.Combine(RepositoryRoot.Locate(), RenderedCard);
        Assert.True(File.Exists(png), $"{RenderedCard} is missing. Run ./scripts/refresh-social-card.sh.");

        var (width, height) = ReadPngSize(png);
        Assert.True(
            width == 1280 && height == 640,
            $"{RenderedCard} is {width}x{height}. GitHub's social preview wants 1280x640, and the "
            + "renderer produces exactly that — a different size means the file was replaced by hand.");
    }

    [Fact]
    public void TheLayoutPointsAtTheRenderedCard()
    {
        var root = RepositoryRoot.Locate();
        var layout = File.ReadAllText(
            Path.Combine(root, "docs", "templates", "stratara", "layout", "_master.tmpl"));

        Assert.Contains("assets/social-card.png", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("og-cover", layout, StringComparison.Ordinal);
    }

    private static (int Width, int Height) ReadPngSize(string path)
    {
        // IHDR is the first chunk of every PNG: width and height are big-endian ints at 16 and 20.
        Span<byte> header = stackalloc byte[24];
        using var stream = File.OpenRead(path);
        stream.ReadExactly(header);

        return (
            BinaryPrimitives.ReadInt32BigEndian(header[16..20]),
            BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
    }
}
