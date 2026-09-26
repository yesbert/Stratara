using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Abstractions.Persistence;

namespace Stratara.EventSourcing.EntityFrameworkCore;

/// <summary>
/// Base <see cref="IUnitOfWork"/> implementation backed by an EF Core
/// <see cref="IDbContextFactory{TContext}"/>. Each <see cref="StartAsync"/> call mints a fresh
/// DbContext that lives for the duration of the returned transaction.
/// </summary>
/// <typeparam name="TDbContext">The concrete DbContext type owned by this unit of work.</typeparam>
/// <remarks>
/// A save the store committed is never reported as cancelled. On a context whose options carry
/// <see cref="CommitCompletionInterceptor"/> — every context the framework registers — a transaction's save honours the
/// caller's token while the changes are written and lets the commit run to its end. On any other — a context a host
/// registered itself without the interceptor, one whose interceptors come from an internal service provider, or one whose
/// <c>AutoTransactionBehavior</c> is <c>Never</c> — the save ignores the token and runs to its end whole, bounded by the
/// connection's pool wait and command timeout rather than by the caller; a host that stops within a shorter shutdown
/// timeout adds the interceptor to such a context.
/// </remarks>
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

        private bool CarriesCommitCompletion =>
            context.Database.AutoTransactionBehavior != AutoTransactionBehavior.Never
            && context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors?.Contains(CommitCompletionInterceptor.Instance) == true;

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // A save the store committed must never be reported as cancelled, or whatever runs it runs it again. A
                // context that carries CommitCompletionInterceptor honours the caller's token while the changes are
                // written and lets the commit run to its end; on one that does not — a context a host registered itself
                // — the save runs to its end whole.
                return await context.SaveChangesAsync(CarriesCommitCompletion ? cancellationToken : CancellationToken.None);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                throw new ConcurrencyConflictException(ex.Message, ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await context.DisposeAsync();
        }
    }
}
