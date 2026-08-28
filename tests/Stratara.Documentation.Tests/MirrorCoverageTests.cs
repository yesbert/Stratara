namespace Stratara.Documentation.Tests;

/// <summary>
/// The generated catalogue is only useful where an agent can fetch it, which is the public mirror.
/// The mirror copies an explicit allowlist, so a file that is not on the list simply never appears
/// — silently, and only visibly to somebody looking for it there.
/// </summary>
public class MirrorCoverageTests
{
    private const string SyncScript = "scripts/sync-to-github.sh";

    [Fact]
    public void TheGeneratedCatalogue_IsOnTheMirrorsFileAllowlist()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot.Locate(), SyncScript));

        Assert.Contains("\"llms-full.txt\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratorsDirectory_IsOnTheMirrorsDirectoryAllowlist()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot.Locate(), SyncScript));

        Assert.Contains("\"tools\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCatalogueExistsAtTheRepositoryRoot() =>
        Assert.True(File.Exists(Path.Combine(RepositoryRoot.Locate(), "llms-full.txt")));
}
