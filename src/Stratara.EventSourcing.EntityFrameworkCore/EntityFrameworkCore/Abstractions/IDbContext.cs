using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

/// <summary>
/// Minimal DbContext surface that Stratara repositories program against, exposing the parts of
/// <see cref="DbContext"/> needed for persistence without depending on the concrete EF Core type.
/// </summary>
/// <remarks>
/// Implemented by the Write, Read, and Identity DbContexts so that repositories and unit-of-work
/// implementations can be constructor-injected without a hard coupling to a specific context type.
/// </remarks>
public interface IDbContext
{
    /// <summary>Gets the database facade for migrations, transactions, and raw SQL.</summary>
    DatabaseFacade Database { get; }

    /// <summary>Gets the EF Core change tracker for the current context.</summary>
    ChangeTracker ChangeTracker { get; }

    /// <summary>Returns a <see cref="DbSet{TEntity}"/> for the given entity type.</summary>
    /// <typeparam name="TEntity">The entity type tracked by the context.</typeparam>
    DbSet<TEntity> Set<TEntity>() where TEntity : class;

    /// <summary>Persists all pending changes to the underlying database asynchronously.</summary>
    /// <param name="token">Token used to cancel the save operation.</param>
    /// <returns>The number of state entries written to the database.</returns>
    Task<int> SaveChangesAsync(CancellationToken token = default);

    /// <summary>Persists all pending changes to the underlying database synchronously.</summary>
    /// <returns>The number of state entries written to the database.</returns>
    int SaveChanges();
}
