namespace Stratara.Documentation.Tests;

/// <summary>
/// One C# code fence taken from a documentation page, with the position it came from so a failure
/// points at the source rather than at the extracted text.
/// </summary>
public sealed record DocumentationSnippet(string SourcePath, int LineNumber, string Code)
{
    public override string ToString() => $"{SourcePath}:{LineNumber}";
}
