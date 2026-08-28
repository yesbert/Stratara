namespace Stratara.Documentation.Tests;

/// <summary>
/// A fence that re-declares a framework type must declare the shape the framework actually has.
/// Showing an interface is how a page explains what a consumer implements — so the page has to be
/// showing the real one.
/// </summary>
public class DocumentationTypeShapeTests
{
    public static TheoryData<string, int> Snippets
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var snippet in All())
            {
                data.Add(snippet.SourcePath, snippet.LineNumber);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Snippets))]
    public void DeclaredType_MatchesTheFramework(string sourcePath, int lineNumber)
    {
        var snippet = All().Single(s => s.SourcePath == sourcePath && s.LineNumber == lineNumber);

        var mismatches = FrameworkTypeShape.Mismatches(snippet);

        Assert.True(
            mismatches.Count == 0,
            $"{sourcePath}:{lineNumber} re-declares a framework type with the wrong shape:{Environment.NewLine}"
            + string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void TheSignerSnippetThatShippedBefore341_IsReportedAsDrifted()
    {
        var snippet = new DocumentationSnippet(
            "docs/guides/hmac-bus-envelope.md",
            1,
            """
            public interface IBusEnvelopeSigner
            {
                string Sign(BusEnvelopeCanonical canonical);
                bool Verify(BusEnvelopeCanonical canonical, string signature);
            }
            """);

        var mismatches = FrameworkTypeShape.Mismatches(snippet);

        Assert.Equal(2, mismatches.Count);
        Assert.Contains(mismatches, m => m.Contains("Sign(BusEnvelopeCanonical)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCurrentSignerSnippet_Matches()
    {
        var snippet = new DocumentationSnippet(
            "docs/guides/hmac-bus-envelope.md",
            1,
            """
            public interface IBusEnvelopeSigner
            {
                string Sign(string payload);
                bool Verify(string payload, string? signature);
            }
            """);

        Assert.Empty(FrameworkTypeShape.Mismatches(snippet));
    }

    private static IReadOnlyList<DocumentationSnippet> All() =>
    [
        .. DocumentationFiles.Enumerate()
            .SelectMany(path => SnippetExtractor.Extract(
                DocumentationFiles.RelativePath(path),
                File.ReadAllText(path)))
    ];
}
