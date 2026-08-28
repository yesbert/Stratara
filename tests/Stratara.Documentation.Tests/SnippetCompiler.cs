using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Stratara.Documentation.Tests;

/// <summary>
/// Compiles a documentation snippet against the framework assemblies. A fence is a fragment, so it
/// is classified first: a fence that declares a type is compiled inside a namespace, and a fence of
/// statements is compiled inside a method body that predeclares the names a host normally has in
/// scope — but only those the fence does not declare itself.
/// </summary>
public static class SnippetCompiler
{
    private static readonly string[] StandardUsings =
    [
        "System",
        "System.Collections.Generic",
        "System.Linq",
        "System.Threading",
        "System.Threading.Tasks",
        "System.Text.Json",
        "System.Text.Json.Nodes",
        "Microsoft.AspNetCore.Identity",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore.Builder",
        "Microsoft.AspNetCore.Http",
        "Microsoft.Extensions.Configuration",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Hosting",
        "Microsoft.Extensions.Logging",
        "Microsoft.Extensions.Options",
        "Microsoft.Extensions.Diagnostics.HealthChecks",
        "Moq",
    ];

    private static readonly (string Name, string Declaration)[] AmbientDeclarations =
    [
        ("args", "string[] args = [];"),
        ("builder", "var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();"),
        ("services", "var services = __host.Services;"),
        ("configuration", "var configuration = __host.Configuration;"),
        ("app", "var app = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder().Build();"),
    ];

    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview, DocumentationMode.None);

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(LoadReferences);

    private static readonly Lazy<IReadOnlyList<string>> FrameworkNamespaces = new(LoadFrameworkNamespaces);

    private static readonly Lazy<SyntaxTree> Placeholders = new(LoadPlaceholders);

    public static IReadOnlyList<string> Compile(DocumentationSnippet snippet) =>
        Compile([], snippet);

    /// <summary>
    /// Compiles <paramref name="snippet"/> together with the fences that precede it on the same
    /// page: a guide builds its scenario across several blocks, and a later block is only a
    /// fragment when read alone.
    /// </summary>
    public static IReadOnlyList<string> Compile(
        IReadOnlyList<DocumentationSnippet> pageContext,
        DocumentationSnippet snippet)
    {
        ArgumentNullException.ThrowIfNull(pageContext);
        ArgumentNullException.ThrowIfNull(snippet);

        var source = Wrap(pageContext, snippet.Code);
        var compilation = CSharpCompilation.Create(
            assemblyName: "DocumentationSnippets",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source, ParseOptions), Placeholders.Value],
            references: References.Value,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        return [.. compilation
            .GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())];
    }

    public static bool DeclaresAType(string code) => Split(code).Declarations.Length > 0;

    /// <summary>
    /// Separates a fence into the types it declares and the statements it runs. A fence often holds
    /// both — a context class and the registration that wires it — and the two belong in different
    /// places in the compiled file.
    /// </summary>
    internal static (string[] Declarations, string[] Statements) Split(string code)
    {
        var members = CSharpSyntaxTree.ParseText(code, ParseOptions).GetCompilationUnitRoot().Members;

        return (
            [.. members
                .Where(member => member is BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax or DelegateDeclarationSyntax)
                .Select(member => member.ToFullString())],
            [.. members
                .OfType<GlobalStatementSyntax>()
                .Select(member => member.ToFullString())]);
    }

    internal static string Wrap(string code) => Wrap([], code);

    internal static string Wrap(IReadOnlyList<DocumentationSnippet> pageContext, string code)
    {
        var usable = pageContext.Where(IsSelfConsistent).ToList();
        var precedingTypes = usable
            .Where(s => !FrameworkTypeShape.ReDeclaresAFrameworkType(s.Code))
            .SelectMany(s => Split(Strip(s.Code)).Declarations)
            .ToList();
        var precedingStatements = usable
            .SelectMany(s => Split(Strip(s.Code)).Statements)
            .ToList();

        var source = new StringBuilder();
        foreach (var @namespace in StandardUsings.Concat(FrameworkNamespaces.Value))
        {
            source.Append("using ").Append(@namespace).AppendLine(";");
        }

        foreach (var directive in usable.Select(s => s.Code).Append(code).SelectMany(UsingDirectives).Distinct(StringComparer.Ordinal))
        {
            source.AppendLine(directive);
        }

        code = Strip(code);

        source.AppendLine("namespace DocumentationSnippet;").AppendLine();

        var (declarations, statements) = Split(code);
        foreach (var declaration in precedingTypes.Concat(declarations))
        {
            source.AppendLine(declaration);
        }

        if (statements.Length == 0)
        {
            return source.ToString();
        }

        var declaredHere = DeclaredNames(string.Join(Environment.NewLine, statements));
        if (AmbientDeclarations.Any(a => a.Name != "args" && declaredHere.Contains(a.Name)))
        {
            precedingStatements.Clear();
        }

        var body = string.Join(Environment.NewLine, [.. precedingStatements, .. statements]);

        source.AppendLine("internal static class Snippet");
        source.AppendLine("{");
        source.AppendLine("    private static async Task RunAsync()");
        source.AppendLine("    {");
        source.AppendLine("        var __host = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();");
        source.AppendLine("        await Task.CompletedTask;");

        var declared = DeclaredNames(body);
        foreach (var (name, declaration) in AmbientDeclarations.Where(a => !declared.Contains(a.Name)))
        {
            source.Append("        ").AppendLine(declaration);
        }

        source.AppendLine(body);
        source.AppendLine("    }");
        source.AppendLine("}");
        return source.ToString();
    }

    private static IReadOnlyList<string> UsingDirectives(string code) =>
        [.. CSharpSyntaxTree.ParseText(code, ParseOptions)
            .GetCompilationUnitRoot()
            .Usings
            .Select(directive => directive.ToFullString().Trim())];

    private static string Strip(string code)
    {
        var root = CSharpSyntaxTree.ParseText(code, ParseOptions).GetCompilationUnitRoot();
        return root.Usings.Count == 0 ? code : root.RemoveNodes(root.Usings, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString();
    }

    private static bool IsSelfConsistent(DocumentationSnippet snippet) =>
        SelfConsistent.GetOrAdd(snippet, s => Compile([], s).Count == 0);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<DocumentationSnippet, bool> SelfConsistent = new();

    private static SyntaxTree LoadPlaceholders()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ConsumerPlaceholders.cs");
        return CSharpSyntaxTree.ParseText(File.ReadAllText(path), ParseOptions);
    }

    private static HashSet<string> DeclaredNames(string code)
    {
        var root = CSharpSyntaxTree.ParseText(code, ParseOptions).GetRoot();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case VariableDeclaratorSyntax declarator:
                    names.Add(declarator.Identifier.ValueText);
                    break;
                case ParameterSyntax parameter:
                    names.Add(parameter.Identifier.ValueText);
                    break;
                case ForEachStatementSyntax forEach:
                    names.Add(forEach.Identifier.ValueText);
                    break;
                case SingleVariableDesignationSyntax designation:
                    names.Add(designation.Identifier.ValueText);
                    break;
                case LocalFunctionStatementSyntax localFunction:
                    names.Add(localFunction.Identifier.ValueText);
                    break;
                default:
                    break;
            }
        }

        return names;
    }

    private static ImmutableArray<MetadataReference> LoadReferences()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
        {
            foreach (var path in trusted.Split(Path.PathSeparator).Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                paths.Add(path);
            }
        }

        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
        {
            paths.Add(path);
        }

        return [.. paths
            .Where(File.Exists)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))];
    }

    private static IReadOnlyList<string> LoadFrameworkNamespaces()
    {
        var namespaces = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "Stratara.*.dll"))
        {
            if (Path.GetFileNameWithoutExtension(path).EndsWith(".Tests", StringComparison.Ordinal))
            {
                continue;
            }

            IEnumerable<Type> types;
            try
            {
                types = Assembly.LoadFrom(path).GetExportedTypes();
            }
            catch (FileLoadException)
            {
                continue;
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            foreach (var type in types.Where(t => t.Namespace is not null))
            {
                namespaces.Add(type.Namespace!);
            }
        }

        return [.. namespaces];
    }
}
