using Microsoft.Extensions.DependencyInjection;

namespace Stratara.Orleans.Hosting;

/// <summary>
/// Refuses a registration of the execution model once the host was built from the service collection: the
/// collection is read-only then, and what the registration adds to — the roles the silo publishes, the works it
/// runs once — is what the running host already reads.
/// </summary>
internal static class BuiltHostGuard
{
    /// <exception cref="InvalidOperationException"><paramref name="services"/> is read-only.</exception>
    public static void Refuse(IServiceCollection services, string registration, string changed)
    {
        if (services.IsReadOnly)
        {
            throw new InvalidOperationException(
                $"{registration} was called after the host was built: the service collection is read-only, and the " +
                $"{changed} would change under it. Call {registration} while the host is being configured.");
        }
    }
}
