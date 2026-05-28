using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Projections.Multitenancy.Models;
using Stratara.Projections.Multitenancy.Repositories;

namespace Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Multitenancy.Repositories;

/// <summary>
/// EF Core-backed <see cref="ITenantRepository"/> over the read-store <see cref="TenantView"/>
/// projection. Read queries are no-tracked; writes target the same <see cref="IReadDbContext"/>
/// scope so projections and queries share the unit of work.
/// </summary>
/// <param name="context">The read-store DbContext that owns the <see cref="TenantView"/> set.</param>
internal sealed class TenantRepository(IReadDbContext context) : ITenantRepository
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<TenantView>> GetAllActiveAsync(CancellationToken cancellationToken = default) =>
        await context.Set<TenantView>()
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TenantView>> GetAllCustomerTenantsAsync(Guid customerId, CancellationToken cancellationToken = default) =>
        await context.Set<TenantView>()
            .AsNoTracking()
            .Where(x => x.CustomerId == customerId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<TenantView?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await context.Set<TenantView>().FindAsync([id], cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default) =>
        await context.Set<TenantView>().AsNoTracking().AnyAsync(x => x.Id == id, cancellationToken);

    /// <inheritdoc/>
    public async Task<Guid> AddAsync(TenantView tenant, CancellationToken cancellationToken = default)
    {
        var entity = await context.Set<TenantView>().AddAsync(tenant, cancellationToken);
        return entity.Entity.Id;
    }

    /// <inheritdoc/>
    public TenantView Update(TenantView tenantView) => context.Set<TenantView>().Update(tenantView).Entity;

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await GetAsync(id, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        context.Set<TenantView>().Remove(entity);
        return true;
    }

    /// <inheritdoc/>
    public Task<int> DeleteCustomerTenantsAsync(Guid customerId, CancellationToken cancellationToken)
    {
        return context.Set<TenantView>().Where(x => x.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
    }
}
