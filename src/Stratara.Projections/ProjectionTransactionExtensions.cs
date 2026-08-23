using Stratara.Abstractions.Persistence;

namespace Stratara.Projections;

/// <summary>
/// Helpers for the write half of an idempotent projection.
/// </summary>
/// <remarks>
/// <para>
/// At-least-once delivery means a projection will see the same event twice, and cascading deletes
/// mean a row can vanish between the read and the write. Neither is a fault. A second writer
/// changing a row that <em>still exists</em> is, and it must keep stopping the bundle.
/// </para>
/// <para>
/// The other half of writing an idempotent projection needs no helper: load the row, and return
/// when it is not there.
/// <code>
/// var tenant = await repository.GetAsync(@event.StreamId, cancellationToken);
/// if (tenant is null) { return; }
/// </code>
/// </para>
/// </remarks>
public static class ProjectionTransactionExtensions
{
    /// <summary>
    /// Commits the transaction, treating a concurrency conflict as satisfied when the row the write
    /// targeted is gone — a concurrent bundle reached the same end state first.
    /// </summary>
    /// <param name="transaction">The active read transaction.</param>
    /// <param name="targetStillExists">
    /// Probes whether the write's target is still present. Evaluated only after a conflict. Return
    /// <see langword="true"/> if any targeted row still exists, which makes the conflict a real one.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of rows the commit affected, or <c>0</c> when the conflict was satisfied.</returns>
    /// <exception cref="ConcurrencyConflictException">
    /// Rethrown when <paramref name="targetStillExists"/> reports the row is still there. Suppressing
    /// it would turn "a failing projection stops the bundle" into a guarantee that holds only where
    /// nobody used this helper.
    /// </exception>
    public static async Task<int> SaveChangesIdempotentAsync(
        this ITransaction transaction,
        Func<CancellationToken, Task<bool>> targetStillExists,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(targetStillExists);

        try
        {
            return await transaction.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            if (await targetStillExists(cancellationToken))
            {
                throw;
            }

            return 0;
        }
    }
}
