using System.Reflection;
using System.Text.RegularExpressions;
using Stratara.Diagnostics;

namespace Stratara.Documentation.Tests;

/// <summary>
/// The allocation table is what a consumer reads to pick an event-ID range that will not collide
/// with the framework's. It stopped three buckets short of the code for two releases.
/// </summary>
public partial class LogEventAllocationTests
{
    private const string SchemaPage = "docs/reference/log-events-schema.md";

    [GeneratedRegex(@"currently allocated `[0-9_]+ – ([0-9_]+)`")]
    private static partial Regex AllocatedRangePattern();

    public static TheoryData<string, int> Buckets
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var (name, bucket) in Allocated())
            {
                data.Add(name, bucket);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Buckets))]
    public void Bucket_HasARowInTheSchemaPage(string name, int bucket)
    {
        var page = DocumentationCorpus.Page(SchemaPage);

        Assert.True(
            page.Contains($"`{bucket / 1000}_000s`", StringComparison.Ordinal),
            $"LogEvents.{name} allocates the {bucket / 1000}_000s, which {SchemaPage} has no row for.");
        Assert.True(
            DocumentationCorpus.MentionsToken(page, $"LogEvents.{name}"),
            $"{SchemaPage} has no row naming LogEvents.{name}.");
    }

    [Fact]
    public void TheStatedUpperBound_MatchesTheHighestAllocatedBucket()
    {
        var page = DocumentationCorpus.Page(SchemaPage);
        var highest = Allocated().Max(entry => entry.Bucket);
        var expected = $"{highest / 1000}_999";

        var stated = AllocatedRangePattern().Match(page);

        Assert.True(stated.Success, $"{SchemaPage} no longer states the allocated range.");
        Assert.Equal(expected, stated.Groups[1].Value);
    }

    private static IReadOnlyList<(string Name, int Bucket)> Allocated() =>
    [
        .. typeof(LogEvents)
            .GetNestedTypes(BindingFlags.Public)
            .Select(nested => (
                Name: nested.Name,
                Bucket: nested
                    .GetFields(BindingFlags.Public | BindingFlags.Static)
                    .Where(field => field.IsLiteral && field.FieldType == typeof(int))
                    .Select(field => (int)field.GetRawConstantValue()!)
                    .DefaultIfEmpty(0)
                    .Min() / 1000 * 1000))
            .Where(entry => entry.Bucket > 0)
            .OrderBy(entry => entry.Bucket)
    ];
}
