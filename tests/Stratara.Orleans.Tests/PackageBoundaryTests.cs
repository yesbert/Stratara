using System.Reflection;
using System.Text.Json;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The runtime package carries the execution model and nothing a consumer's persistence choice
/// decides: no Entity Framework assembly and not the server meta-package, which the host brings. And
/// no package below it depends on the runtime, so a consumer that stays on the bus workers receives
/// none of it. The closure is walked from the runtime package's own entry in this project's
/// dependency graph, so what the test project references beside it does not count.
/// </summary>
public sealed class PackageBoundaryTests
{
    [Fact]
    public void The_runtime_package_brings_no_Entity_Framework_and_not_the_server_meta_package()
    {
        var libraries = DependencyClosureOf("Stratara.Orleans");

        Assert.DoesNotContain(libraries, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(libraries, name => name.StartsWith("Npgsql", StringComparison.Ordinal));
        Assert.DoesNotContain("Stratara.EventSourcing.EntityFrameworkCore", libraries);
        Assert.DoesNotContain("Microsoft.Orleans.Server", libraries);
        Assert.Contains("Microsoft.Orleans.Runtime", libraries);
    }

    [Theory]
    [InlineData("Stratara.Projections")]
    [InlineData("Stratara.Sagas")]
    [InlineData("Stratara.Abstractions")]
    public void A_package_below_the_runtime_references_no_Orleans_assembly(string package)
    {
        var offenders = ReferencedClosure(package)
            .Where(name => name.StartsWith("Orleans", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0, $"{package} reaches {string.Join(", ", offenders)}.");
    }

    [Theory]
    [InlineData("Stratara.Projections")]
    [InlineData("Stratara.Sagas")]
    [InlineData("Stratara.Abstractions")]
    public void A_package_below_the_runtime_brings_no_Orleans_package(string package)
    {
        var offenders = DependencyClosureOf(package)
            .Where(name => name.StartsWith("Microsoft.Orleans", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0, $"{package} brings {string.Join(", ", offenders)}.");
    }

    private static HashSet<string> DependencyClosureOf(string root)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Stratara.Orleans.Tests.deps.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var target = document.RootElement.GetProperty("targets").EnumerateObject().First().Value;
        var graph = target.EnumerateObject().ToDictionary(
            library => library.Name[..library.Name.IndexOf('/')],
            library => library.Value.TryGetProperty("dependencies", out var dependencies)
                ? dependencies.EnumerateObject().Select(dependency => dependency.Name).ToList()
                : [],
            StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var name))
        {
            if (!seen.Add(name) || !graph.TryGetValue(name, out var dependencies))
            {
                continue;
            }

            foreach (var dependency in dependencies)
            {
                pending.Push(dependency);
            }
        }

        seen.Remove(root);
        return seen;
    }

    private static HashSet<string> ReferencedClosure(string root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<AssemblyName>([new AssemblyName(root)]);
        while (pending.TryPop(out var name))
        {
            if (name.Name is null || !seen.Add(name.Name) || !name.Name.StartsWith("Stratara.", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var reference in Assembly.Load(name).GetReferencedAssemblies())
            {
                pending.Push(reference);
            }
        }

        return seen;
    }
}
