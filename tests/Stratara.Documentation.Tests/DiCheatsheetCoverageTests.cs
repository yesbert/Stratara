namespace Stratara.Documentation.Tests;

/// <summary>
/// Every registration a host can call appears in the cheatsheet. Absence is the expensive failure
/// mode: <c>AddRedisOutboxLock</c> — without which a second outbox-worker replica is not safe —
/// appeared on no page at all, and nothing said so.
/// </summary>
public class DiCheatsheetCoverageTests
{
    private const string CheatsheetPage = "docs/reference/di-extensions-cheatsheet.md";

    public static TheoryData<string> Registrations
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in RegistrationSurface.Names())
            {
                data.Add(name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public void Registration_IsListedOrAllowlisted(string name)
    {
        if (Allowlist().ContainsKey(name))
        {
            return;
        }

        Assert.True(
            DocumentationCorpus.MentionsToken(DocumentationCorpus.Page(CheatsheetPage), name),
            $"'{name}' is a registration a host can call and {CheatsheetPage} does not list it. "
            + "Add a row, or add it to registration-coverage-allowlist.txt with a reason.");
    }

    [Fact]
    public void EveryAllowlistEntry_StillResolvesToARegistration()
    {
        var names = RegistrationSurface.Names();

        foreach (var (name, reason) in Allowlist())
        {
            Assert.True(
                names.Contains(name, StringComparer.Ordinal),
                $"The allowlist exempts '{name}' ({reason}), which is no longer a registration. Remove the line.");
        }
    }

    private static IReadOnlyDictionary<string, string> Allowlist()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "registration-coverage-allowlist.txt");

        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('—', 2, StringSplitOptions.TrimEntries))
            .ToDictionary(parts => parts[0], parts => parts.Length > 1 ? parts[1] : string.Empty, StringComparer.Ordinal);
    }
}
