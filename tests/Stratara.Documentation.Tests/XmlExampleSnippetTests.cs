using System.Xml.Linq;

namespace Stratara.Documentation.Tests;

/// <summary>
/// The examples that ship inside the packages compile, on the same terms as the fences in
/// <c>docs/</c>. A worked example a reader pastes has to work; one that only looks right is worse
/// than none, because it is followed with confidence.
/// </summary>
public class XmlExampleSnippetTests
{
    public static TheoryData<string> Examples
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var key in All().Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                data.Add(key);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void Example_Compiles(string key)
    {
        var errors = SnippetCompiler.Compile(new DocumentationSnippet(key, 1, All()[key]));

        Assert.True(
            errors.Count == 0,
            $"The <example> on {key} does not compile:{Environment.NewLine}"
            + string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void ThePackagesCarryExamplesToCheck() => Assert.NotEmpty(All());

    private static IReadOnlyDictionary<string, string> All()
    {
        var examples = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "Stratara.*.xml"))
        {
            var assembly = Path.GetFileNameWithoutExtension(path);
            if (assembly.EndsWith(".Tests", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var member in XDocument.Load(path).Descendants("member"))
            {
                var name = member.Attribute("name")?.Value;
                if (name is null || !name.StartsWith("M:", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var (code, index) in member.Descendants("example").Descendants("code").Select((c, i) => (c.Value, i)))
                {
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        examples[$"{assembly} {name}#{index}"] = code;
                    }
                }
            }
        }

        return examples;
    }
}
