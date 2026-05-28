using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Session;

namespace Stratara.Infrastructure.Multitenancy;

/// <summary>
/// <see cref="ITenantService"/> implementation that returns the data-owner <c>TenantId</c> from
/// the ambient <see cref="ISessionContextProvider"/> — used by EF Core's tenant-query-filter to
/// route reads to the subject's tenant, not the actor's tenant.
/// </summary>
internal sealed class TenantService(ISessionContextProvider provider) : ITenantService
{
    /// <inheritdoc/>
    public Guid GetTenantId() => provider.Current?.TenantId ?? Guid.Empty;

}
