using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Projections;
using Stratara.Projections.Multitenancy.Repositories;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Multitenancy.Repositories;
using Stratara.Abstractions.Persistence;

namespace Stratara.EventSourcing.EntityFrameworkCore.ReadStore;

/// <summary>
/// Read-side unit of work for projection writers — extends <see cref="ReadUnitOfWork{TDbContext}"/>
/// with factory methods for projection-specific repositories such as <see cref="ITenantRepository"/>.
/// </summary>
/// <typeparam name="TDbContext">The concrete read-store DbContext type.</typeparam>
/// <param name="contextFactory">Factory used to create a new DbContext per transaction.</param>
public class ProjectionsUnitOfWork<TDbContext>(IDbContextFactory<TDbContext> contextFactory)
    : ReadUnitOfWork<TDbContext>(contextFactory), IProjectionsUnitOfWork where TDbContext : DbContext, IReadDbContext
{
    /// <inheritdoc/>
    public ITenantRepository CreateTenantRepository(ITransaction transaction)
    {
        var dbContext = GetDbContext(transaction);
        return new TenantRepository(dbContext);
    }
}
