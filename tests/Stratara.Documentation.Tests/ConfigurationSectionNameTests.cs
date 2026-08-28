namespace Stratara.Documentation.Tests;

/// <summary>
/// Every configuration section the framework binds is named in the documentation by the name it
/// actually binds. The bus-envelope integrity guide documented a <c>BusIntegrity</c> section for
/// three releases; the bound section is <c>BusEnvelopeIntegrity</c>.
/// </summary>
public class ConfigurationSectionNameTests
{
    public static TheoryData<string, string> Sections
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (type, sectionName) in FrameworkSurface.OptionsWithSectionName())
            {
                data.Add(type.Name, sectionName);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Sections))]
    public void SectionName_IsDocumentedSomewhere(string typeName, string sectionName)
    {
        var pages = DocumentationCorpus.PagesMentioning(sectionName);

        Assert.True(
            pages.Count > 0,
            $"'{typeName}' binds configuration section '{sectionName}', which no page under docs/ names. "
            + "A reader who binds the section by the name a page shows would bind nothing.");
    }

    [Fact]
    public void ATypeReferenceDoesNotCountAsNamingTheSection()
    {
        const string before = """
            Call `services.AddBusEnvelopeIntegrity(...)`. See `BusEnvelopeIntegrityOptions` for the
            `Off` / `Permissive` / `Strict` modes.

            ```jsonc
            { "BusIntegrity": { "Mode": "Strict" } }
            ```
            """;

        Assert.False(DocumentationCorpus.MentionsToken(before, "BusEnvelopeIntegrity"));
    }

    [Fact]
    public void TheSectionNameInAConfigurationBlockCounts()
    {
        const string after = """
            ```jsonc
            { "BusEnvelopeIntegrity": { "Mode": "Strict" } }
            ```
            """;

        Assert.True(DocumentationCorpus.MentionsToken(after, "BusEnvelopeIntegrity"));
    }
}
