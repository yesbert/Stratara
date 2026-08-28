namespace Stratara.Documentation.Tests;

public class DocumentationFilesTests
{
    [Fact]
    public void Enumerate_FindsTheHandWrittenPages()
    {
        var files = DocumentationFiles.Enumerate();

        Assert.NotEmpty(files);
        Assert.Contains(files, path => DocumentationFiles.RelativePath(path) == "docs/index.md");
    }

    [Fact]
    public void Enumerate_ExcludesGeneratedOutput()
    {
        var relative = DocumentationFiles.Enumerate().Select(DocumentationFiles.RelativePath).ToList();

        Assert.DoesNotContain(relative, path => path.StartsWith("docs/_site/", StringComparison.Ordinal));
        Assert.DoesNotContain(relative, path => path.StartsWith("docs/reference/api/", StringComparison.Ordinal));
    }
}
