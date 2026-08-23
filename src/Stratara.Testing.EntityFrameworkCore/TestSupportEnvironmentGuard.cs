using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Stratara.Testing.EntityFrameworkCore;

internal static class TestSupportEnvironmentGuard
{
    private const string DotnetEnvironmentVariable = "DOTNET_ENVIRONMENT";
    private const string AspNetCoreEnvironmentVariable = "ASPNETCORE_ENVIRONMENT";

    internal static void EnsureDevelopmentOrUnstated(IServiceCollection services, string entryPoint)
        => EnsureDevelopmentOrUnstated(services, entryPoint, Environment.GetEnvironmentVariable);

    internal static void EnsureDevelopmentOrUnstated(
        IServiceCollection services,
        string entryPoint,
        Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        var (environmentName, source) = StatedEnvironment(services, readEnvironmentVariable);

        if (environmentName is null)
        {
            return;
        }

        if (string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{entryPoint} is test-support composition and refuses to register into a '{environmentName}' host " +
            $"(stated by {source}). It wires an in-memory SQLite database and in-memory doubles, so a running " +
            "system would start successfully and lose every write. Compose the real event-sourcing stack against " +
            "a real store instead. Where no environment is stated at all — an ordinary unit test — this call is " +
            "allowed.");
    }

    private static (string? Name, string? Source) StatedEnvironment(
        IServiceCollection services,
        Func<string, string?> readEnvironmentVariable)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IHostEnvironment) &&
                descriptor.ImplementationInstance is IHostEnvironment hostEnvironment)
            {
                return (hostEnvironment.EnvironmentName, "the registered host environment");
            }
        }

        var dotnetEnvironment = readEnvironmentVariable(DotnetEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(dotnetEnvironment))
        {
            return (dotnetEnvironment, DotnetEnvironmentVariable);
        }

        var aspNetCoreEnvironment = readEnvironmentVariable(AspNetCoreEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(aspNetCoreEnvironment))
        {
            return (aspNetCoreEnvironment, AspNetCoreEnvironmentVariable);
        }

        return (null, null);
    }
}
