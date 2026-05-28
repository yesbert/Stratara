using Stratara.Projections.Multitenancy.Models;

namespace Stratara.Projections.Multitenancy.Repositories;

/// <summary>
/// Read-store repository for <see cref="TenantView"/>. Implementations live in the EFCore package and are
/// resolved via <see cref="IProjectionsUnitOfWork"/>.
/// </summary>
public interface ITenantRepository
{
    /// <summary>Returns every active tenant in the store.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<TenantView>> GetAllActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns every tenant — active or not — belonging to the given customer.</summary>
    /// <param name="customerId">The customer identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<TenantView>> GetAllCustomerTenantsAsync(Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>Loads the tenant view by identifier, or <c>null</c> if no row exists.</summary>
    /// <param name="id">The tenant identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<TenantView?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns <c>true</c> if a tenant view exists for the given identifier.</summary>
    /// <param name="id">The tenant identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Inserts a new tenant view and returns its identifier.</summary>
    /// <param name="tenant">The view to insert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Guid> AddAsync(TenantView tenant, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing tenant view in the change tracker. Changes are persisted on transaction commit.</summary>
    /// <param name="tenantView">The view to update.</param>
    TenantView Update(TenantView tenantView);

    /// <summary>Deletes the tenant view with the given identifier. Returns <c>true</c> if a row was removed.</summary>
    /// <param name="id">The tenant identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Deletes every tenant belonging to the given customer. Returns the number of rows removed.</summary>
    /// <param name="customerId">The customer identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> DeleteCustomerTenantsAsync(Guid customerId, CancellationToken cancellationToken);
}
