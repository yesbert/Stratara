using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace Stratara.Documentation.Tests;

/// <summary>
/// A type a page names in inline code exists. Three guides named <c>SagaOrchestrationWorker</c>,
/// <c>EventProjectionWorker</c> and <c>OptimisticConcurrencyException</c> — none of which has ever
/// shipped — and no check read prose. Only framework-shaped names are checked: an identifier with a
/// suffix the framework uses for types a consumer registers, catches or configures, or one that starts
/// with <c>Stratara</c>. Example types a page invents (<c>AccountOpened</c>) have no such shape.
/// A name passes when an assembly the test can see declares it as a type, when a published assembly
/// declares it as a method (registration extensions carry the same suffixes) or as a string constant (a
/// source, meter or scheme name is a value pages name in code), or when it is an assembly, a namespace or a fully qualified type; when a
/// project or solution in the repository has that name; when a sample under <c>samples/</c> declares
/// it; or when a fenced block on the same page declares it.
/// </summary>
public partial class InlineTypeNameTests
{
    private static readonly string[] FrameworkSuffixes =
    [
        "Worker", "Exception", "Options", "Provider", "Service", "Dispatcher", "Repository",
        "Store", "Behavior", "Middleware", "Attribute", "Host", "Tester",
    ];

    private static readonly Dictionary<string, string> Allowlist = new(StringComparer.Ordinal)
    {
    };

    private static readonly Lazy<HashSet<string>> KnownNames = new(LoadKnownNames);

    public static TheoryData<string> Pages
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var page in DocumentationCorpus.All.Keys)
            {
                data.Add(page);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Pages), DisableDiscoveryEnumeration = true)]
    public void FrameworkShapedNamesInInlineCode_Exist(string page)
    {
        var unknown = UnknownNames(DocumentationCorpus.Page(page));

        Assert.True(
            unknown.Count == 0,
            $"{page} names types in inline code that no assembly, sample or fenced block on the page "
            + $"declares: {string.Join(" | ", unknown)} — "
            + "A reader who searches for them finds nothing.");
    }

    [Theory]
    [InlineData("SagaOrchestrationWorker")]
    [InlineData("EventProjectionWorker")]
    [InlineData("OptimisticConcurrencyException")]
    public void ANameThatNeverShipped_IsReported(string name)
    {
        Assert.Equal([name], UnknownNames($"The `{name}` handles it."));
    }

    [Fact]
    public void ATypeThatShipped_Passes()
    {
        Assert.Empty(UnknownNames("Use `TestSessionContextProvider.ForTenant(tenantId)` in a test."));
    }

    [Fact]
    public void ATypeDeclaredInAFenceOnThePage_Passes()
    {
        const string page = """
            The `TransferStore` below keeps the rows.

            ```csharp
            public sealed class TransferStore { }
            ```
            """;

        Assert.Empty(UnknownNames(page));
    }

    [Fact]
    public void GenericArgumentsAndMemberAccessAreStripped()
    {
        Assert.Empty(UnknownNames("Bind `IOptions<MessagingOptions>` and read `MessagingOptions.SectionName`."));
        Assert.Equal(["MissingOptions"], UnknownNames("Read `MissingOptions.SectionName`."));
    }

    internal static IReadOnlyList<string> UnknownNames(string markdown)
    {
        var fences = FencePattern().Matches(markdown).Select(m => m.Value).ToList();
        var declared = fences
            .SelectMany(fence => DeclarationPattern().Matches(fence).Select(m => m.Groups["name"].Value))
            .ToHashSet(StringComparer.Ordinal);
        var prose = FencePattern().Replace(markdown, string.Empty);

        return [.. InlineCodePattern().Matches(prose)
            .Select(m => LeadingName(m.Groups["code"].Value))
            .Where(name => name is not null && IsFrameworkShaped(name))
            .Select(name => name!)
            .Where(name => !declared.Contains(name) && !Allowlist.ContainsKey(name) && !KnownNames.Value.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)];
    }

    private static string? LeadingName(string code)
    {
        var match = LeadingNamePattern().Match(code.Trim());
        if (!match.Success)
        {
            return null;
        }

        var name = match.Groups["name"].Value;
        return name.StartsWith("Stratara.", StringComparison.Ordinal) ? name : name.Split('.')[0];
    }

    private static bool IsFrameworkShaped(string name) =>
        name.StartsWith("Stratara", StringComparison.Ordinal)
        || FrameworkSuffixes.Any(suffix => name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal));

    private static HashSet<string> LoadKnownNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var directories = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(object).Assembly.Location)!,
            Path.GetDirectoryName(typeof(Microsoft.AspNetCore.Http.HttpContext).Assembly.Location)!,
        };

        foreach (var path in directories.Distinct(StringComparer.Ordinal).SelectMany(d => Directory.EnumerateFiles(d, "*.dll")))
        {
            var assemblyName = Path.GetFileNameWithoutExtension(path);
            if (assemblyName.EndsWith(".Tests", StringComparison.Ordinal) || assemblyName == "Stratara.ReferenceCatalogue")
            {
                continue;
            }

            AddTypeNames(path, names, includeMembers: FrameworkSurface.Published.Any(a => a.GetName().Name == assemblyName));
        }

        AddSampleDeclarations(names);
        AddRepositoryProjects(names);
        return names;
    }

    private static void AddTypeNames(string path, HashSet<string> names, bool includeMembers)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return;
            }

            var reader = pe.GetMetadataReader();
            if (reader.IsAssembly)
            {
                AddNamespaceAndPrefixes(reader.GetString(reader.GetAssemblyDefinition().Name), names);
            }

            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                var name = reader.GetString(type.Name);
                var tick = name.IndexOf('`', StringComparison.Ordinal);
                names.Add(tick < 0 ? name : name[..tick]);

                var ns = reader.GetString(type.Namespace);
                AddNamespaceAndPrefixes(ns, names);
                if (ns.Length > 0)
                {
                    names.Add($"{ns}.{(tick < 0 ? name : name[..tick])}");
                }

                if (includeMembers)
                {
                    foreach (var methodHandle in type.GetMethods())
                    {
                        names.Add(reader.GetString(reader.GetMethodDefinition(methodHandle).Name));
                    }

                    AddStringConstants(reader, type, names);
                }
            }
        }
        catch (BadImageFormatException)
        {
        }
    }

    private static void AddStringConstants(MetadataReader reader, TypeDefinition type, HashSet<string> names)
    {
        foreach (var fieldHandle in type.GetFields())
        {
            var field = reader.GetFieldDefinition(fieldHandle);
            var constantHandle = field.GetDefaultValue();
            if (constantHandle.IsNil)
            {
                continue;
            }

            var constant = reader.GetConstant(constantHandle);
            if (constant.TypeCode != ConstantTypeCode.String)
            {
                continue;
            }

            var blob = reader.GetBlobReader(constant.Value);
            var value = blob.ReadUTF16(blob.Length);
            if (value.Length > 0)
            {
                names.Add(value);
            }
        }
    }

    private static void AddNamespaceAndPrefixes(string dotted, HashSet<string> names)
    {
        var segments = dotted.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i <= segments.Length; i++)
        {
            names.Add(string.Join('.', segments[..i]));
        }
    }

    private static void AddRepositoryProjects(HashSet<string> names)
    {
        var root = RepositoryRoot.Locate();
        foreach (var pattern in new[] { "*.csproj", "*.slnx", "*.slnf" })
        {
            foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                names.Add(Path.GetFileNameWithoutExtension(file));
                names.Add(Path.GetFileName(file));
            }
        }
    }

    private static void AddSampleDeclarations(HashSet<string> names)
    {
        var samples = Path.Combine(RepositoryRoot.Locate(), "samples");
        foreach (var file in Directory.EnumerateFiles(samples, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in DeclarationPattern().Matches(File.ReadAllText(file)))
            {
                names.Add(match.Groups["name"].Value);
            }
        }
    }

    [GeneratedRegex(@"^(```|~~~).*?^\1", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"\b(?:class|record|interface|struct|enum)\s+(?:struct\s+|class\s+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex DeclarationPattern();

    [GeneratedRegex(@"(?<!`)`(?<code>[^`\r\n]+)`(?!`)")]
    private static partial Regex InlineCodePattern();

    [GeneratedRegex(@"^@?(?<name>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)")]
    private static partial Regex LeadingNamePattern();
}
