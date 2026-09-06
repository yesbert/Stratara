using System.Text.RegularExpressions;

namespace Stratara.Documentation.Tests;

/// <summary>
/// <c>docs/templates/stratara/layout/_master.tmpl</c> is a copy of one DocFX version's modern
/// template, taken because canonical links, Open Graph tags, a skip link and the description repair
/// have no configuration key between them. A copy does not inherit what upstream improves, so the
/// deploy workflow installs that exact DocFX version rather than the newest.
/// <para>
/// This test holds the two together. It cannot tell that upstream changed the file — nothing short
/// of vendoring the original could — but it makes a DocFX upgrade a deliberate act: whoever raises
/// the pin is told, by a failure here, that a forked file is waiting for them.
/// </para>
/// </summary>
public partial class DocfxForkProvenanceTests
{
    private const string ForkedLayout = "docs/templates/stratara/layout/_master.tmpl";
    private const string DeployWorkflow = ".github/workflows/deploy-site.yml";

    [GeneratedRegex(@"docfx-template-version:\s*(?<version>\d+\.\d+\.\d+)")]
    private static partial Regex ForkVersionPattern();

    [GeneratedRegex(@"dotnet tool install -g docfx --version\s+(?<version>\d+\.\d+\.\d+)")]
    private static partial Regex PinnedVersionPattern();

    [Fact]
    public void TheForkNamesTheVersionTheWorkflowPins()
    {
        var root = RepositoryRoot.Locate();

        var fork = ForkVersionPattern().Match(File.ReadAllText(Path.Combine(root, ForkedLayout)));
        Assert.True(
            fork.Success,
            $"{ForkedLayout} carries no 'docfx-template-version:' line. That comment is the only "
            + "record of which upstream template this file was copied from.");

        var pin = PinnedVersionPattern().Match(File.ReadAllText(Path.Combine(root, DeployWorkflow)));
        Assert.True(
            pin.Success,
            $"{DeployWorkflow} installs DocFX without --version. With a forked layout in the tree "
            + "an unpinned install lets a DocFX release move the original out from under the fork.");

        Assert.True(
            fork.Groups["version"].Value == pin.Groups["version"].Value,
            $"The fork was taken from DocFX {fork.Groups["version"].Value} but the deploy pins "
            + $"{pin.Groups["version"].Value}. Re-take the copy from the pinned version and "
            + "re-apply the blocks marked STRATARA, then update the comment.");
    }
}
