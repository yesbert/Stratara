using System.Reflection;
using System.Runtime.CompilerServices;

namespace Stratara.Documentation.Tests;

/// <summary>
/// The registrations a host calls. Keyed on the type a method extends rather than on an
/// <c>Add</c> prefix: a name-based rule sweeps in <c>AddRangeAsync</c> and <c>MapTo</c> and misses
/// nothing useful in exchange.
/// </summary>
public static class RegistrationSurface
{
    private static readonly string[] ExtendedTypes =
    [
        "IServiceCollection",
        "IHostApplicationBuilder",
        "IHealthChecksBuilder",
        "AuthenticationBuilder",
        "IApplicationBuilder",
        "WebApplication",
    ];

    private static readonly Lazy<IReadOnlyList<MethodInfo>> Methods = new(Load);

    public static IReadOnlyList<MethodInfo> Enumerate() => Methods.Value;

    public static IReadOnlyList<string> Names() =>
        [.. Methods.Value.Select(method => method.Name).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal)];

    private static IReadOnlyList<MethodInfo> Load() =>
    [
        .. FrameworkSurface.Published
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type.IsSealed && type.IsAbstract)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(method => method.IsDefined(typeof(ExtensionAttribute), inherit: false))
            .Where(Extends)
    ];

    private static bool Extends(MethodInfo method)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 0)
        {
            return false;
        }

        var first = parameters[0].ParameterType;
        var name = first.IsGenericParameter
            ? string.Join(",", first.GetGenericParameterConstraints().Select(constraint => constraint.Name))
            : first.Name;

        return ExtendedTypes.Any(candidate => name.Contains(candidate, StringComparison.Ordinal));
    }
}
