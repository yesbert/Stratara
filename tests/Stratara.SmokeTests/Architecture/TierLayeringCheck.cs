using System.Xml.Linq;

namespace Stratara.SmokeTests.Architecture;

/// <summary>
/// Asserts the tier rule over the declared project references: Tier-N may reference only Tier-≤N,
/// and there are no cycles. The rule had no mechanical enforcement — a foundational package could
/// gain a higher-tier reference and nothing would fail, shipping infrastructure dependencies to
/// every lean consumer until one of them noticed.
/// </summary>
/// <remarks>
/// It reads the project files rather than the built assemblies deliberately. A package's NuGet
/// dependency comes from its <c>ProjectReference</c>, and the compiler drops references whose types
/// the code never touches — so an assembly-metadata check misses exactly the case that hurts: a
/// declared reference that ships as a dependency without a call site to justify it. That case is
/// real in this repository, which is how the weaker version of this check was caught.
/// </remarks>
public static class TierLayeringCheck
{
    private static readonly Dictionary<string, int> TierByProject = new(StringComparer.Ordinal)
    {
        ["Stratara.Abstractions"] = 1,
        ["Stratara.Contracts"] = 1,
        ["Stratara.Diagnostics"] = 1,
        ["Stratara.Resilience"] = 1,

        ["Stratara.Mediator"] = 2,
        ["Stratara.Domain"] = 2,
        ["Stratara.Shared"] = 2,
        ["Stratara.Sessions"] = 2,
        ["Stratara.ServiceDefaults"] = 2,

        ["Stratara.EventSourcing.EntityFrameworkCore"] = 3,
        ["Stratara.EventSourcing.Pipeline.CommandAudit"] = 3,
        ["Stratara.EventSourcing.WorkerDefaults"] = 3,
        ["Stratara.Validation"] = 3,
        ["Stratara.Projections"] = 3,
        ["Stratara.Sagas"] = 3,
        ["Stratara.Security"] = 3,
        ["Stratara.Outbox.RabbitMQ"] = 3,
        ["Stratara.Outbox.AzureServiceBus"] = 3,
        ["Stratara.Infrastructure"] = 3,
        ["Stratara.Identity.Core"] = 3,
        ["Stratara.Identity.AspNetCore"] = 3,
        ["Stratara.Identity.EntityFrameworkCore"] = 3,
        ["Stratara.ServiceDefaults.AspNetCore"] = 3
    };

    private static readonly string[] TierNames = ["", "A", "B", "C"];

    public static void Run()
    {
        var sourceDirectory = Path.Combine(RepositoryRoot(), "src");
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string> violations = [];

        foreach (var (project, tier) in TierByProject.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var file = Path.Combine(sourceDirectory, project, $"{project}.csproj");
            if (!File.Exists(file))
            {
                throw new InvalidOperationException(
                    $"{project}.csproj was not found under {sourceDirectory}. The tier check can only see the " +
                    "projects it can read, so a renamed or moved project would silently narrow it to nothing.");
            }

            var references = ReferencedStrataraProjects(file);
            edges[project] = references;

            foreach (var reference in references.Where(reference => TierByProject[reference] > tier))
            {
                violations.Add(
                    $"{project} (Tier-{TierNames[tier]}) references {reference} " +
                    $"(Tier-{TierNames[TierByProject[reference]]})");
            }
        }

        var cycle = FindCycle(edges);
        if (cycle is not null)
        {
            violations.Add($"reference cycle: {string.Join(" -> ", cycle)}");
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                "Tier layering is violated. Tier-N may reference only Tier-<=N:" +
                Environment.NewLine + string.Join(Environment.NewLine, violations.Order(StringComparer.Ordinal)));
        }

        Console.WriteLine($"Tier layering holds across {TierByProject.Count} runtime projects, no cycles.");
    }

    private static List<string> ReferencedStrataraProjects(string csprojPath) =>
        [.. XDocument.Load(csprojPath)
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => include is not null)
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
            .Where(TierByProject.ContainsKey)];

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stratara.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException(
            "Could not locate the repository root (no Stratara.slnx found walking up from " +
            $"{AppContext.BaseDirectory}).");
    }

    private static List<string>? FindCycle(Dictionary<string, List<string>> edges)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        List<string> path = [];

        foreach (var node in edges.Keys)
        {
            var cycle = Walk(node, edges, state, path);
            if (cycle is not null)
            {
                return cycle;
            }
        }

        return null;
    }

    private static List<string>? Walk(
        string node,
        Dictionary<string, List<string>> edges,
        Dictionary<string, int> state,
        List<string> path)
    {
        if (state.TryGetValue(node, out var seen))
        {
            if (seen != 1)
            {
                return null;
            }

            var start = path.IndexOf(node);
            return [.. path[start..], node];
        }

        state[node] = 1;
        path.Add(node);

        foreach (var next in edges.GetValueOrDefault(node, []))
        {
            var cycle = Walk(next, edges, state, path);
            if (cycle is not null)
            {
                return cycle;
            }
        }

        path.RemoveAt(path.Count - 1);
        state[node] = 2;
        return null;
    }
}
