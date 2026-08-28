namespace Stratara.Documentation.Tests;

/// <summary>
/// Every C# fence in the hand-written documentation compiles against the framework it documents.
/// A fence that cannot compile — because it is consumer-side code, a deliberate fragment or an
/// illustration — carries an ignore directive naming the reason.
/// </summary>
public class DocumentationSnippetsCompileTests
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
    public void Snippet_Compiles(string sourcePath, int lineNumber)
    {
        var page = All().Where(s => s.SourcePath == sourcePath).ToList();
        var snippet = page.Single(s => s.LineNumber == lineNumber);
        var context = page.Where(s => s.LineNumber < lineNumber).ToList();

        var errors = SnippetCompiler.Compile(context, snippet);

        Assert.True(
            errors.Count == 0,
            $"{sourcePath}:{lineNumber} does not compile:{Environment.NewLine}"
            + string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void TheDocumentationCarriesSnippetsToCheck()
    {
        Assert.NotEmpty(All());
    }

    private static IReadOnlyList<DocumentationSnippet> All() =>
    [
        .. DocumentationFiles.Enumerate()
            .SelectMany(path => SnippetExtractor.Extract(
                DocumentationFiles.RelativePath(path),
                File.ReadAllText(path)))
    ];
}
