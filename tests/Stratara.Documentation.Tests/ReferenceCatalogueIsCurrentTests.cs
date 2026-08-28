namespace Stratara.Documentation.Tests;

/// <summary>
/// The committed catalogue matches what the generator produces from the assemblies as they are
/// now. A generated file that is committed can go stale in a branch; this is what stops it.
/// </summary>
public class ReferenceCatalogueIsCurrentTests
{
    private const string CatalogueFile = "llms-full.txt";

    [Fact]
    public void TheCommittedCatalogue_MatchesTheAssemblies()
    {
        var path = Path.Combine(RepositoryRoot.Locate(), CatalogueFile);
        var committed = File.ReadAllText(path).ReplaceLineEndings("\n");
        var regenerated = ReferenceCatalogue.ReferenceCatalogue.Render(AppContext.BaseDirectory);

        Assert.True(
            committed == regenerated,
            $"{CatalogueFile} is out of date. Regenerate it with "
            + "'dotnet run --project tools/Stratara.ReferenceCatalogue -- llms-full.txt' and commit the result.");
    }

    [Fact]
    public void TheCatalogueCoversTheFourCategories()
    {
        var committed = File.ReadAllText(Path.Combine(RepositoryRoot.Locate(), CatalogueFile));

        Assert.Contains("## Configuration", committed, StringComparison.Ordinal);
        Assert.Contains("## Registrations", committed, StringComparison.Ordinal);
        Assert.Contains("## Exceptions", committed, StringComparison.Ordinal);
        Assert.Contains("## Names on the wire", committed, StringComparison.Ordinal);
    }
}
