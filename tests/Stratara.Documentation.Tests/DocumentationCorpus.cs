using System.Text.RegularExpressions;

namespace Stratara.Documentation.Tests;

/// <summary>
/// The hand-written documentation as text, with the one matching rule these assertions share:
/// a name counts as documented only where it stands on its own. <c>BusEnvelopeIntegrity</c> inside
/// <c>BusEnvelopeIntegrityOptions</c> is a type reference, not a statement about a configuration
/// section — reading it as one is how a wrong section name survived three releases.
/// </summary>
public static class DocumentationCorpus
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Pages = new(Load);

    public static IReadOnlyDictionary<string, string> All => Pages.Value;

    public static string Page(string relativePath) => Pages.Value[relativePath];

    public static bool MentionsToken(string text, string token) =>
        Regex.IsMatch(text, $@"(?<![A-Za-z0-9_]){Regex.Escape(token)}(?![A-Za-z0-9_])");

    public static IReadOnlyList<string> PagesMentioning(string token) =>
        [.. Pages.Value.Where(page => MentionsToken(page.Value, token)).Select(page => page.Key).OrderBy(p => p, StringComparer.Ordinal)];

    private static IReadOnlyDictionary<string, string> Load() =>
        DocumentationFiles.Enumerate().ToDictionary(
            DocumentationFiles.RelativePath,
            File.ReadAllText,
            StringComparer.Ordinal);
}
