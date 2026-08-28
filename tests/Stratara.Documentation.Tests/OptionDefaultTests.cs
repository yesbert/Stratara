using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Stratara.Documentation.Tests;

/// <summary>
/// A default a page states is the default the type carries. The number is what a reader sizes a
/// deployment against, and it is the kind of fact that survives the change that invalidates it.
/// </summary>
public class OptionDefaultTests
{
    public static TheoryData<string> OptionTypes
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var type in Instantiable())
            {
                data.Add(type.FullName!);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(OptionTypes))]
    public void StatedDefaults_MatchTheType(string typeName)
    {
        var type = Instantiable().Single(candidate => candidate.FullName == typeName);
        var instance = Activator.CreateInstance(type)!;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanRead && IsNumeric(p.PropertyType)))
        {
            var actual = Convert.ToString(property.GetValue(instance), CultureInfo.InvariantCulture);
            if (actual is null)
            {
                continue;
            }

            foreach (var (page, stated) in StatedDefaults(type, property.Name))
            {
                Assert.True(
                    stated == actual,
                    $"{page} states a default of {stated} for {type.Name}.{property.Name}; the type carries {actual}.");
            }
        }
    }

    [Fact]
    public void TheOptionTypesAreFound()
    {
        Assert.Contains(Instantiable(), type => type.Name == "OutboxOptions");
    }

    /// <summary>
    /// A property name that several options types share is only attributable where the page
    /// qualifies it — <c>OutboxOptions.BatchSize</c> and <c>ProjectionOptions.BatchSize</c> are
    /// different numbers, and a bare mention names neither.
    /// </summary>
    private static IEnumerable<(string Page, string Stated)> StatedDefaults(Type type, string propertyName)
    {
        var qualifier = IsAmbiguous(propertyName)
            ? $"{Regex.Escape(type.Name)}\\."
            : $"(?:{Regex.Escape(type.Name)}\\.)?";

        var pattern = new Regex(
            $@"(?<![A-Za-z0-9_]){qualifier}{Regex.Escape(propertyName)}(?![A-Za-z0-9_])[^.\n]{{0,70}}?[Dd]efaults?\D{{0,12}}?([0-9][0-9_]*)",
            RegexOptions.None);

        foreach (var (page, text) in DocumentationCorpus.All)
        {
            foreach (Match match in pattern.Matches(text))
            {
                yield return (page, match.Groups[1].Value.Replace("_", string.Empty, StringComparison.Ordinal));
            }
        }
    }

    private static bool IsAmbiguous(string propertyName) =>
        Instantiable()
            .Count(type => type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance) is not null) > 1;

    private static bool IsNumeric(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(decimal);

    private static IReadOnlyList<Type> Instantiable() =>
    [
        .. FrameworkSurface.ExportedTypes
            .Where(type => type.Name.EndsWith("Options", StringComparison.Ordinal))
            .Where(type => !type.IsAbstract && !type.IsGenericTypeDefinition && type.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
    ];
}
