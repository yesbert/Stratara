using System.Reflection;
using System.Xml.Linq;

namespace Stratara.Documentation.Tests;

/// <summary>
/// Every registration carries a worked example in the XML documentation that ships beside its
/// assembly. That file is what a consumer's editor shows at the call site, and the only channel
/// available to tooling in a repository that cannot read this source tree.
/// </summary>
public class RegistrationDocumentationTests
{
    public static TheoryData<string> Registrations
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var key in Documented().Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                data.Add(key);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public void Registration_CarriesAnExample(string key)
    {
        Assert.True(
            Documented()[key],
            $"'{key}' is a registration and its shipped XML documentation carries no <example>. "
            + "State the configuration key path it binds, its prerequisites, and one worked example — "
            + "see the API documentation policy.");
    }

    [Fact]
    public void AnObsoleteRegistrationIsNotRequiredToCarryOne()
    {
        var obsolete = RegistrationSurface.Enumerate()
            .Where(method => method.IsDefined(typeof(ObsoleteAttribute), inherit: false))
            .Select(method => method.Name);

        Assert.DoesNotContain(Documented().Keys, key => obsolete.Any(name => key.EndsWith($".{name}", StringComparison.Ordinal)));
    }

    private static IReadOnlyDictionary<string, bool> Documented()
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var method in RegistrationSurface.Enumerate())
        {
            if (method.IsDefined(typeof(ObsoleteAttribute), inherit: false))
            {
                continue;
            }

            var assembly = method.DeclaringType!.Assembly;
            var key = $"{assembly.GetName().Name}.{method.Name}";
            result[key] = result.TryGetValue(key, out var already) && already || HasExample(assembly, method.Name);
        }

        return result;
    }

    private static bool HasExample(Assembly assembly, string methodName)
    {
        var path = Path.ChangeExtension(assembly.Location, ".xml");
        if (!File.Exists(path))
        {
            return false;
        }

        return XDocument.Load(path)
            .Descendants("member")
            .Where(member => (member.Attribute("name")?.Value ?? string.Empty)
                .Contains($".{methodName}(", StringComparison.Ordinal)
                || (member.Attribute("name")?.Value ?? string.Empty)
                .Contains($".{methodName}``", StringComparison.Ordinal))
            .Any(member => member.Element("example") is { } example && !string.IsNullOrWhiteSpace(example.Value));
    }
}
