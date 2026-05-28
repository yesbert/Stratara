using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Abstractions.Persistence;

namespace Stratara.EventSourcing.EntityFrameworkCore;

/// <summary>
/// Base <see cref="IUnitOfWork"/> implementation backed by an EF Core
/// <see cref="IDbContextFactory{TContext}"/>. Each <see cref="StartAsync"/> call mints a fresh
/// DbContext that lives for the duration of the returned transaction.
/// </summary>
/// <typeparam name="TDbContext">The concrete DbContext type owned by this unit of work.</typeparam>
/// <param name="contextFactory">Factory used to create a new DbContext per transaction.</param>
public class UnitOfWork<TDbContext>(IDbContextFactory<TDbContext> contextFactory) : IUnitOfWork where TDbContext : DbContext, IDbContext
{
    /// <inheritdoc/>
    public async Task<ITransaction> StartAsync(CancellationToken cancellationToken = default)
    {
        var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return new EfTransaction(context);
    }

    /// <summary>
    /// Extracts the underlying <typeparamref name="TDbContext"/> from a transaction previously
    /// returned by <see cref="StartAsync"/>. Used by derived unit-of-work classes when wiring
    /// their repositories.
    /// </summary>
    /// <param name="transaction">A transaction created by this unit of work.</param>
    /// <returns>The DbContext that owns the transaction's change tracker.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="transaction"/> was not created by this unit of work.</exception>
    protected static TDbContext GetDbContext(ITransaction transaction) => transaction is not EfTransaction efTransaction
        ? throw new ArgumentException("Transaction must be of type EfTransaction", nameof(transaction))
        : efTransaction.DbContext;

    private sealed class EfTransaction(TDbContext context) : ITransaction
    {
        internal TDbContext DbContext => context;

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var count = await context.SaveChangesAsync(cancellationToken);
            return count;
        }

        public async ValueTask DisposeAsync()
        {
            await context.DisposeAsync();
        }
    }
}
