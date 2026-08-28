namespace Stratara.Documentation.Tests;

public class SnippetExtractorTests
{
    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Extract_TakesCSharpFencesOnly()
    {
        var snippets = SnippetExtractor.Extract("Fixtures/snippets.md", ReadFixture("snippets.md"));

        Assert.Equal(2, snippets.Count);
        Assert.Contains(snippets, s => s.Code.Contains("public sealed record Widget", StringComparison.Ordinal));
        Assert.Contains(snippets, s => s.Code.Contains("var widget = new Widget", StringComparison.Ordinal));
        Assert.DoesNotContain(snippets, s => s.Code.Contains("Widgets", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_SkipsAFenceMarkedIgnored()
    {
        var snippets = SnippetExtractor.Extract("Fixtures/snippets.md", ReadFixture("snippets.md"));

        Assert.DoesNotContain(snippets, s => s.Code.Contains("not valid C#", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_ReportsThePositionOfEachFence()
    {
        var snippets = SnippetExtractor.Extract("Fixtures/snippets.md", ReadFixture("snippets.md"));

        Assert.All(snippets, s => Assert.True(s.LineNumber > 0));
        Assert.Equal("Fixtures/snippets.md:5", snippets[0].ToString());
    }

    [Fact]
    public void Extract_RejectsAnIgnoreDirectiveWithoutAReason()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => SnippetExtractor.Extract("Fixtures/unreasoned-ignore.md", ReadFixture("unreasoned-ignore.md")));

        Assert.Contains("must state a reason", exception.Message, StringComparison.Ordinal);
    }
}
