namespace Stratara.Documentation.Tests;

public class RepositoryRootTests
{
    [Fact]
    public void Locate_ReturnsTheDirectoryHoldingThePublishSolutionFilter()
    {
        var root = RepositoryRoot.Locate();

        Assert.True(File.Exists(Path.Combine(root, "Stratara.Publish.slnf")));
    }
}
