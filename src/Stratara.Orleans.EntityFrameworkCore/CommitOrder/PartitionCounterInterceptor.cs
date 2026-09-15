using Stratara.Abstractions.CommitOrder;
using Stratara.Orleans.CommitOrder;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Orleans.EntityFrameworkCore.CommitOrder;

/// <summary>
/// Maintains the partition counter inside the transaction that appends entries: before the entries
/// are written, the counter row of each touched partition is incremented and locked, and the
/// positions it hands out are stamped on the entries. The lock is held until the commit, so no
/// later transaction in the same partition can commit first — which is what lets the portable reader
/// order by position and never skip.
/// </summary>
/// <remarks>
/// Where the save runs outside a transaction the interceptor opens one and commits it after the
/// save, so the counter update and the insert always share a transaction. The write path itself is
/// unchanged: this is an EF Core interceptor the context registers, not a change to how entries are
/// added. The cost of the lock is the throughput ceiling the benchmarks measure.
/// </remarks>
public sealed class PartitionCounterInterceptor(int partitionCount) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, IDbContextTransaction> _ownedTransactions = new();

    /// <inheritdoc/>
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context is null)
        {
            return result;
        }

        var added = context.ChangeTracker.Entries<EventStreamEntry>()
            .Where(entry => entry.State == EntityState.Added)
            .ToList();
        if (added.Count == 0)
        {
            return result;
        }

        if (context.Database.CurrentTransaction is null)
        {
            _ownedTransactions.Add(context, await context.Database.BeginTransactionAsync(cancellationToken));
        }

        foreach (var group in added.GroupBy(entry => PartitionMap.PartitionOf(entry.Entity.BucketId, partitionCount)).OrderBy(group => group.Key))
        {
            await StampPositionsAsync(context, group.Key, [.. group], cancellationToken);
        }

        return result;
    }

    /// <inheritdoc/>
    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _ownedTransactions.TryGetValue(context, out var transaction))
        {
            _ownedTransactions.Remove(context);
            await transaction.CommitAsync(cancellationToken);
            await transaction.DisposeAsync();
        }

        return result;
    }

    /// <inheritdoc/>
    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _ownedTransactions.TryGetValue(context, out var transaction))
        {
            _ownedTransactions.Remove(context);
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
        }
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always — the framework's write path is asynchronous, and the counter is only maintained on it.</exception>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
        throw new NotSupportedException("The partition counter is maintained on the asynchronous save path only; use SaveChangesAsync.");

    private static async Task StampPositionsAsync(DbContext context, int partition, IReadOnlyList<EntityEntry<EventStreamEntry>> entries, CancellationToken cancellationToken)
    {
        var count = entries.Count;
        var updated = await context.Database.ExecuteSqlAsync(
            $"UPDATE partition_position SET position = position + {count} WHERE partition = {partition}",
            cancellationToken);
        if (updated != 1)
        {
            throw new InvalidOperationException(
                $"Partition {partition} has no counter row in {CommitOrderModel.PartitionPositionTable}; seed one row per partition before appending.");
        }

        var last = await context.Set<PartitionPosition>().AsNoTracking()
            .Where(counter => counter.Partition == partition)
            .Select(counter => counter.Position)
            .SingleAsync(cancellationToken);

        var position = last - count;
        foreach (var entry in entries)
        {
            position++;
            entry.Property(CommitOrderModel.PartitionPositionColumn).CurrentValue = position;
        }
    }
}
