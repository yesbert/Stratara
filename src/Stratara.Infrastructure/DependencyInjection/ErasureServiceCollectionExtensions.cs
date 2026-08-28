using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.Erasure;
using Stratara.Infrastructure.Security;

namespace Stratara.Infrastructure.DependencyInjection;

/// <summary>
/// Registers the composed erasure operation.
/// </summary>
public static class ErasureServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISubjectEraser"/>, which composes the membership, API-key, setting and
    /// key-material sweeps into one operation. The four stores it sweeps must be registered
    /// separately — this call adds no store of its own.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// The four stores it sweeps must already be registered — this call adds none of them:
    /// <code>
    /// services.AddTenantMembershipStore&lt;DirectoryDbContext&gt;();
    /// services.AddApiKeyStore&lt;DirectoryDbContext&gt;();
    /// services.AddSettingStore&lt;DirectoryDbContext&gt;();
    /// services.AddStrataraErasure();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraErasure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<ISubjectEraser, SubjectEraser>();

        return services;
    }
}
