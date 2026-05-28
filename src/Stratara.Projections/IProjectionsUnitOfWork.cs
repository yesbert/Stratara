using Stratara.Projections.Multitenancy.Repositories;
using Stratara.Abstractions.Persistence;

namespace Stratara.Projections;

/// <summary>
/// Read-side unit-of-work used by projections to access the projection read store. Extends
/// <see cref="IReadUnitOfWork"/> with factory methods for the repositories that live in this package.
/// </summary>
public interface IProjectionsUnitOfWork : IReadUnitOfWork
{
    /// <summary>Creates an <see cref="ITenantRepository"/> bound to the supplied read transaction.</summary>
    /// <param name="transaction">An active read transaction obtained from <see cref="IUnitOfWork.StartAsync"/>.</param>
    ITenantRepository CreateTenantRepository(ITransaction transaction);
}
