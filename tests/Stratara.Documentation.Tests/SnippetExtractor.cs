using System.Text;

namespace Stratara.Documentation.Tests;

/// <summary>
/// Pulls the C# fences out of a markdown page. A fence preceded by
/// <c>&lt;!-- stratara-snippet-ignore: reason --&gt;</c> is skipped; the reason is mandatory so
/// every exemption states why it is one.
/// </summary>
public static class SnippetExtractor
{
    public const string IgnoreDirective = "stratara-snippet-ignore";

    private static readonly string[] CSharpLanguages = ["csharp", "cs", "c#"];

    public static IReadOnlyList<DocumentationSnippet> Extract(string sourcePath, string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var snippets = new List<DocumentationSnippet>();
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            var language = trimmed[3..].Trim().ToLowerInvariant();
            var openedAt = index;
            var body = new StringBuilder();

            index++;
            while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                body.AppendLine(lines[index]);
                index++;
            }

            if (!CSharpLanguages.Contains(language))
            {
                continue;
            }

            var ignoreReason = ReadIgnoreReason(lines, openedAt, sourcePath);
            if (ignoreReason is not null)
            {
                continue;
            }

            snippets.Add(new DocumentationSnippet(sourcePath, openedAt + 1, body.ToString()));
        }

        return snippets;
    }

    private static string? ReadIgnoreReason(string[] lines, int fenceIndex, string sourcePath)
    {
        var candidate = fenceIndex > 0 ? lines[fenceIndex - 1].Trim() : string.Empty;
        if (!candidate.Contains(IgnoreDirective, StringComparison.Ordinal))
        {
            return null;
        }

        var separator = candidate.IndexOf(':', StringComparison.Ordinal);
        var reason = separator < 0
            ? string.Empty
            : candidate[(separator + 1)..].Replace("-->", string.Empty, StringComparison.Ordinal).Trim();

        if (reason.Length == 0)
        {
            throw new InvalidOperationException(
                $"{sourcePath}:{fenceIndex} — '{IgnoreDirective}' must state a reason, as in "
                + $"'<!-- {IgnoreDirective}: consumer-side ASP.NET call, not framework surface -->'.");
        }

        return reason;
    }
}
