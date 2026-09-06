using System.Text.RegularExpressions;

namespace Stratara.Documentation.Tests;

/// <summary>
/// The site is the project's public homepage, so what a crawler and an answer engine read from it
/// is part of the product. Two properties are worth holding onto, and neither survives on
/// discipline alone.
/// <para>
/// The first is that every hand-written page describes itself. Before 2026-09-06 fifty-one of them
/// shared one sentence from <c>globalMetadata</c>, which a search engine discards and replaces with
/// a guess. The generated API reference is excluded deliberately: a sentence per type would have to
/// be invented rather than written, and the global fallback is the honest answer there.
/// </para>
/// <para>
/// The second is that a page emits exactly one description. DocFX's own layout emitted the global
/// tag and the page tag as two independent sections, so a page carrying front matter served both.
/// The forked layout repairs that, and this asserts the repair rather than the symptom: checking
/// the built HTML would mean building the site inside a unit test, and the template is where the
/// guarantee actually lives.
/// </para>
/// </summary>
public partial class SiteMetadataTests
{
    private const string ForkedLayout = "docs/templates/stratara/layout/_master.tmpl";

    [GeneratedRegex(@"^---\r?\n(.*?)\r?\n---", RegexOptions.Singleline)]
    private static partial Regex FrontMatterPattern();

    [GeneratedRegex(@"^description:\s*""?(?<value>.*?)""?\s*$", RegexOptions.Multiline)]
    private static partial Regex DescriptionPattern();

    [Fact]
    public void EveryHandWrittenPage_DescribesItself()
    {
        var offenders = DocumentationFiles.Enumerate()
            .Where(file => !HasNonEmptyDescription(File.ReadAllText(file)))
            .Select(DocumentationFiles.RelativePath)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These pages carry no front-matter description, so they ship the global sentence that "
            + "every other page ships and a search result shows a guess instead: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void TheForkedLayout_EmitsOneDescription_WithTheGlobalOneAsFallback()
    {
        var layout = File.ReadAllText(Path.Combine(RepositoryRoot.Locate(), ForkedLayout));

        var tags = Regex.Matches(layout, """<meta name="description""").Count;
        Assert.True(
            tags == 2,
            $"The layout writes {tags} description tags. There must be exactly two — the page's own "
            + "and the global fallback — and they must exclude each other, which is what the "
            + "assertion below pins.");

        Assert.Contains(
            """{{^description}}{{#_description}}<meta name="description""",
            layout,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheTechArticleBlock_IsGatedOnTheFrontMatterTitle()
    {
        var layout = File.ReadAllText(Path.Combine(RepositoryRoot.Locate(), ForkedLayout));

        // Gating on the description looks equivalent and is not. DocFX gives an API reference page
        // a description of its own, taken from the type's XML summary, but never a title — so the
        // description gate emitted a TechArticle with an empty headline on 421 generated pages.
        // A title only ever comes from front matter, which only a hand-written page has.
        Assert.Contains(
            """{{^_isLanding}}{{#title}}{{#description}}""",
            layout,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RobotsTxt_NamesTheSitemapAndBlocksNothing()
    {
        var robots = File.ReadAllText(Path.Combine(RepositoryRoot.Locate(), "docs", "robots.txt"));

        Assert.Contains("Sitemap: https://stratara.tech/sitemap.xml", robots, StringComparison.Ordinal);

        // A blanket disallow shipped by accident deindexes the site, and getting back in takes
        // weeks. It is the one change in this area that is not cheap to undo.
        Assert.DoesNotContain("Disallow: /", robots, StringComparison.Ordinal);
    }

    private static bool HasNonEmptyDescription(string markdown)
    {
        var frontMatter = FrontMatterPattern().Match(markdown);
        if (!frontMatter.Success)
        {
            return false;
        }

        var description = DescriptionPattern().Match(frontMatter.Groups[1].Value);
        return description.Success && description.Groups["value"].Value.Trim().Length > 0;
    }
}
