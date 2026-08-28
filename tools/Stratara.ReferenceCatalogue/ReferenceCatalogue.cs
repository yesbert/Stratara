using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace Stratara.ReferenceCatalogue;

/// <summary>
/// Renders the machine-readable slice of Stratara's surface — the facts a consumer's tooling cannot
/// recover from the package alone: which configuration key sets what, what a registration needs
/// before it works, what the framework throws, and the names it uses on a broker or a cache.
/// Everything is derived from the assemblies and their documentation, so it cannot drift from them.
/// </summary>
public static class ReferenceCatalogue
{
    public static string Render(string assemblyDirectory)
    {
        var assemblies = Load(assemblyDirectory);
        var documentation = LoadDocumentation(assemblyDirectory);
        var output = new StringBuilder();

        Header(output);
        ConfigurationSection(output, assemblies);
        RegistrationSection(output, assemblies, documentation);
        ExceptionSection(output, assemblies, documentation);
        NameSection(output, assemblies);

        return output.ToString().ReplaceLineEndings("\n");
    }

    private static void Header(StringBuilder output)
    {
        output.AppendLine("# Stratara — generated reference");
        output.AppendLine();
        output.AppendLine("Generated from the published assemblies and their XML documentation. Do not edit by hand;");
        output.AppendLine("a build regenerates it and fails on a difference. The guides live in `docs/`, the guarantees");
        output.AppendLine("in the capability specifications — this file is the lookup table underneath both.");
        output.AppendLine();
    }

    private static void ConfigurationSection(StringBuilder output, IReadOnlyList<Assembly> assemblies)
    {
        output.AppendLine("## Configuration");
        output.AppendLine();
        output.AppendLine("Every bindable options type, the section it binds from, and the default each key carries.");
        output.AppendLine();

        foreach (var type in assemblies.SelectMany(a => a.GetExportedTypes())
                     .Where(type => SectionNameOf(type) is not null)
                     .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            var section = SectionNameOf(type)!;
            output.AppendLine($"### `{section}` — {type.FullName}");
            output.AppendLine();
            output.AppendLine("| Key | Type | Default |");
            output.AppendLine("|---|---|---|");

            var instance = type.GetConstructor(Type.EmptyTypes) is null ? null : Activator.CreateInstance(type);

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(property => property.CanRead)
                         .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                var value = instance is null ? null : property.GetValue(instance);
                output.AppendLine(
                    $"| `{section}:{property.Name}` | `{FriendlyName(property.PropertyType)}` | {Format(value)} |");
            }

            output.AppendLine();
        }
    }

    private static void RegistrationSection(
        StringBuilder output,
        IReadOnlyList<Assembly> assemblies,
        IReadOnlyDictionary<string, string> documentation)
    {
        output.AppendLine("## Registrations");
        output.AppendLine();
        output.AppendLine("Public extension methods a host calls to wire Stratara, with what each one does and needs.");
        output.AppendLine();
        output.AppendLine("| Registration | Package | What it does |");
        output.AppendLine("|---|---|---|");

        foreach (var method in assemblies
                     .SelectMany(assembly => assembly.GetExportedTypes())
                     .Where(type => type is { IsSealed: true, IsAbstract: true })
                     .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                     .Where(IsRegistration)
                     .OrderBy(method => method.Name, StringComparer.Ordinal)
                     .ThenBy(method => method.GetParameters().Length))
        {
            var package = method.DeclaringType!.Assembly.GetName().Name;
            var summary = documentation.GetValueOrDefault(Key(method), "—");
            output.AppendLine($"| `{Signature(method)}` | {package} | {summary} |");
        }

        output.AppendLine();
    }

    private static void ExceptionSection(
        StringBuilder output,
        IReadOnlyList<Assembly> assemblies,
        IReadOnlyDictionary<string, string> documentation)
    {
        output.AppendLine("## Exceptions");
        output.AppendLine();
        output.AppendLine("What the framework throws. Catching one of these is a contract; catching `Exception` is not.");
        output.AppendLine();
        output.AppendLine("| Exception | Package | Thrown when |");
        output.AppendLine("|---|---|---|");

        foreach (var type in assemblies
                     .SelectMany(assembly => assembly.GetExportedTypes())
                     .Where(type => typeof(Exception).IsAssignableFrom(type))
                     .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            var summary = documentation.GetValueOrDefault($"T:{type.FullName}", "—");
            output.AppendLine($"| `{type.FullName}` | {type.Assembly.GetName().Name} | {summary} |");
        }

        output.AppendLine();
    }

    private static void NameSection(StringBuilder output, IReadOnlyList<Assembly> assemblies)
    {
        output.AppendLine("## Names on the wire");
        output.AppendLine();
        output.AppendLine("Identifiers Stratara uses outside its own process — what an operator provisions and what a");
        output.AppendLine("cache holds. Topic and subscription names are the fallbacks used when `Messaging:Topics`");
        output.AppendLine("configures nothing.");
        output.AppendLine();
        output.AppendLine("| Name | Kind | Declared by |");
        output.AppendLine("|---|---|---|");

        foreach (var (name, kind, owner) in WireNames(assemblies).OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            output.AppendLine($"| `{name}` | {kind} | {owner} |");
        }

        output.AppendLine();
    }

    private static IEnumerable<(string Name, string Kind, string Owner)> WireNames(IReadOnlyList<Assembly> assemblies)
    {
        var identifier = MessagingDefaults();
        foreach (var (property, value) in identifier)
        {
            yield return (value, property.Contains("Subscription", StringComparison.Ordinal) ? "subscription" : "topic", "MessagingIdentifier");
        }

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes().OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                             .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                             .OrderBy(field => field.Name, StringComparer.Ordinal))
                {
                    if (field.GetRawConstantValue() is not string value
                        || !value.StartsWith("stratara:", StringComparison.Ordinal)
                        || value.Length == "stratara:".Length)
                    {
                        continue;
                    }

                    yield return (
                        value,
                        type.Name.Contains("Claim", StringComparison.Ordinal) ? "claim type" : "cache key",
                        type.Name);
                }
            }
        }
    }

    private static IEnumerable<(string Property, string Value)> MessagingDefaults()
    {
        var optionsType = Type.GetType("Stratara.Shared.Messaging.MessagingOptions, Stratara.Shared");
        var identifierType = Type.GetType("Stratara.Shared.Messaging.MessagingIdentifier, Stratara.Shared");
        if (optionsType is null || identifierType is null)
        {
            yield break;
        }

        var options = Activator.CreateInstance(optionsType)!;
        var wrapper = typeof(Microsoft.Extensions.Options.Options)
            .GetMethod(nameof(Microsoft.Extensions.Options.Options.Create))!
            .MakeGenericMethod(optionsType)
            .Invoke(null, [options]);

        var identifier = Activator.CreateInstance(identifierType, wrapper)!;

        foreach (var property in identifierType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.PropertyType == typeof(string))
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            if (property.GetValue(identifier) is string value)
            {
                yield return (property.Name, value);
            }
        }
    }

    private static bool IsRegistration(MethodInfo method)
    {
        if (!method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), inherit: false))
        {
            return false;
        }

        if (method.IsDefined(typeof(ObsoleteAttribute), inherit: false))
        {
            return false;
        }

        var parameters = method.GetParameters();
        if (parameters.Length == 0)
        {
            return false;
        }

        var first = parameters[0].ParameterType;
        var name = first.IsGenericParameter
            ? string.Join(",", first.GetGenericParameterConstraints().Select(constraint => constraint.Name))
            : first.Name;

        return name.Contains("IServiceCollection", StringComparison.Ordinal)
            || name.Contains("IHostApplicationBuilder", StringComparison.Ordinal)
            || name.Contains("IHealthChecksBuilder", StringComparison.Ordinal)
            || name.Contains("AuthenticationBuilder", StringComparison.Ordinal)
            || name.Contains("IApplicationBuilder", StringComparison.Ordinal)
            || name.Contains("WebApplication", StringComparison.Ordinal);
    }

    private static string Signature(MethodInfo method)
    {
        var generics = method.IsGenericMethodDefinition
            ? "<" + string.Join(", ", method.GetGenericArguments().Select(argument => argument.Name)) + ">"
            : string.Empty;

        var parameters = string.Join(
            ", ",
            method.GetParameters().Skip(1).Select(parameter => $"{FriendlyName(parameter.ParameterType)} {parameter.Name}"));

        return $"{method.Name}{generics}({parameters})";
    }

    private static string? SectionNameOf(Type type)
    {
        var field = type.GetField("SectionName", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        return field?.IsLiteral == true && field.GetRawConstantValue() is string value && value.Length > 0 ? value : null;
    }

    private static string Format(object? value) => value switch
    {
        null => "—",
        string text when text.Length == 0 => "*(empty)*",
        string text => $"`{text}`",
        bool flag => $"`{(flag ? "true" : "false")}`",
        Array array => array.Length == 0 ? "*(empty)*" : $"`{array.Length} entries`",
        _ => $"`{Convert.ToString(value, CultureInfo.InvariantCulture)}`",
    };

    private static string FriendlyName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return FriendlyName(underlying) + "?";
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FriendlyName))}>";
    }

    private static string Key(MethodInfo method) => $"M:{method.DeclaringType!.FullName}.{method.Name}";

    private static IReadOnlyList<Assembly> Load(string directory) =>
    [
        .. Directory.EnumerateFiles(directory, "Stratara.*.dll")
            .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith(".Tests", StringComparison.Ordinal))
            .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith(".ReferenceCatalogue", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(Assembly.LoadFrom)
    ];

    /// <summary>
    /// Renders a documentation element as text. A naive read of <c>Value</c> drops every
    /// <c>&lt;see cref&gt;</c>, which is where the type names are — leaving sentences with holes
    /// exactly at the nouns that carry them.
    /// </summary>
    private static string Flatten(XElement element)
    {
        var text = new StringBuilder();

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText content:
                    text.Append(content.Value);
                    break;
                case XElement child when child.Name == "see" || child.Name == "seealso":
                    text.Append(Reference(child));
                    break;
                case XElement child when child.Name == "paramref" || child.Name == "typeparamref":
                    text.Append(child.Attribute("name")?.Value ?? string.Empty);
                    break;
                case XElement child:
                    text.Append(Flatten(child));
                    break;
                default:
                    break;
            }
        }

        return text.ToString();
    }

    private static string Reference(XElement element)
    {
        if (element.Attribute("langword") is { } langword)
        {
            return langword.Value;
        }

        var cref = element.Attribute("cref")?.Value ?? element.Value;
        var name = cref.Contains(':', StringComparison.Ordinal) ? cref.Split(':', 2)[1] : cref;
        name = name.Split('(')[0];
        var last = name.LastIndexOf('.');
        return last >= 0 ? name[(last + 1)..] : name;
    }

    private static IReadOnlyDictionary<string, string> LoadDocumentation(string directory)
    {
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(directory, "Stratara.*.xml").OrderBy(path => path, StringComparer.Ordinal))
        {
            foreach (var member in XDocument.Load(path).Descendants("member"))
            {
                var name = member.Attribute("name")?.Value;
                if (name is null || member.Element("summary") is not { } summary)
                {
                    continue;
                }

                var text = string.Join(" ", Flatten(summary).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                var sentence = text.Split(". ", 2)[0].TrimEnd('.');
                var key = name.Split('(')[0].Split("``")[0];
                summaries.TryAdd(key, sentence.Replace("|", "\\|", StringComparison.Ordinal));
            }
        }

        return summaries;
    }
}
