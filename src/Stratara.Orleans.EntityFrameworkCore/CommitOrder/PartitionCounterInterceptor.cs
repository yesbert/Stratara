using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using Stratara.Abstractions.CommitOrder;
using Stratara.Orleans.CommitOrder;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Orleans.EntityFrameworkCore.CommitOrder;

/// <summary>
/// Maintains the partition counter inside the transaction that appends entries: before the entries
/// are written, the counter row of each touched partition is incremented and locked, and the
/// positions it hands out are stamped on the entries. The lock is held until the commit, so no
/// later transaction in the same partition can commit first — which is what lets the portable reader
/// order by position and never skip. Within one save, the positions follow each stream's version order.
/// </summary>
/// <remarks>
/// Where the save runs outside a transaction the interceptor opens one and commits it after the
/// save, so the counter update and the insert always share a transaction. The write path itself is
/// unchanged: this is an EF Core interceptor the context registers, not a change to how entries are
/// added. The cost of the lock is the throughput ceiling the benchmarks measure. The partition count is
/// the one every reader is configured with, read from the same options.
/// </remarks>
public sealed class PartitionCounterInterceptor(IOptions<CommitOrderOptions> options) : SaveChangesInterceptor
{
    private readonly int _partitionCount = options.Value.PartitionCount;
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

        var added = InStreamOrder(context.ChangeTracker.Entries<EventStreamEntry>().Where(entry => entry.State == EntityState.Added));
        if (added.Count == 0)
        {
            return result;
        }

        if (context.Database.CurrentTransaction is null)
        {
            _ownedTransactions.AddOrUpdate(context, await context.Database.BeginTransactionAsync(cancellationToken));
        }

        try
        {
            foreach (var group in added.GroupBy(entry => PartitionMap.PartitionOf(entry.Entity.BucketId, _partitionCount)).OrderBy(group => group.Key))
            {
                await StampPositionsAsync(context, group.Key, [.. group], cancellationToken);
            }
        }
        catch
        {
            // The runtime does not report a failure of this callback to SaveChangesFailedAsync, so the transaction this
            // interceptor opened is released here; otherwise the context's next save would run inside it and never commit.
            // The caller's token is not forwarded: a cancelled save must still roll back.
            await ReleaseAsync(context, commit: false, CancellationToken.None);
            throw;
        }

        return result;
    }

    /// <inheritdoc/>
    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            await ReleaseAsync(context, commit: true, cancellationToken);
        }

        return result;
    }

    /// <inheritdoc/>
    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            await ReleaseAsync(context, commit: false, cancellationToken);
        }
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">The save appends event stream entries — the framework's write path is asynchronous, and the counter is only maintained on it. A synchronous save that appends none passes.</exception>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        var appends = eventData.Context?.ChangeTracker.Entries<EventStreamEntry>().Any(entry => entry.State == EntityState.Added) ?? false;
        return appends
            ? throw new NotSupportedException("The partition counter is maintained on the asynchronous save path only; use SaveChangesAsync.")
            : result;
    }

    /// <summary>
    /// Commits or rolls back the transaction this interceptor opened for the context, and always disposes and forgets
    /// it — disposing a transaction whose commit failed rolls it back — so no path leaves it open on the context.
    /// </summary>
    private async Task ReleaseAsync(DbContext context, bool commit, CancellationToken cancellationToken = default)
    {
        if (!_ownedTransactions.TryGetValue(context, out var transaction))
        {
            return;
        }

        try
        {
            if (commit)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }
        finally
        {
            _ownedTransactions.Remove(context);
            await transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// The added entries in the order positions are handed out: the streams in the order they first appear in the
    /// tracker, each stream's entries in version order — the tracker's own order is not a contract.
    /// </summary>
    private static List<EntityEntry<EventStreamEntry>> InStreamOrder(IEnumerable<EntityEntry<EventStreamEntry>> added)
    {
        var entries = added.ToList();
        var firstAppearance = new Dictionary<Guid, int>();
        foreach (var entry in entries)
        {
            firstAppearance.TryAdd(entry.Entity.StreamId, firstAppearance.Count);
        }

        return [.. entries.OrderBy(entry => firstAppearance[entry.Entity.StreamId]).ThenBy(entry => entry.Entity.Version)];
    }

    private static async Task StampPositionsAsync(DbContext context, int partition, IReadOnlyList<EntityEntry<EventStreamEntry>> entries, CancellationToken cancellationToken)
    {
        var count = entries.Count;
        // Through the model rather than as SQL text, so a context that maps the counter away from the
        // snake-case convention locks and advances the row it actually has.
        var updated = await context.Set<PartitionPosition>()
            .Where(counter => counter.Partition == partition)
            .ExecuteUpdateAsync(set => set.SetProperty(counter => counter.Position, counter => counter.Position + count), cancellationToken);
        if (updated != 1)
        {
            var table = context.Model.FindEntityType(typeof(PartitionPosition))?.GetTableName() ?? CommitOrderSchema.PartitionPositionTable;
            throw new InvalidOperationException(
                $"Partition {partition} has no counter row in {table}; seed one row per partition before appending.");
        }

        var last = await context.Set<PartitionPosition>().AsNoTracking()
            .Where(counter => counter.Partition == partition)
            .Select(counter => counter.Position)
            .SingleAsync(cancellationToken);

        var position = last - count;
        foreach (var entry in entries)
        {
            position++;
            entry.Property(CommitOrderSchema.PartitionPositionColumn).CurrentValue = position;
        }
    }
}
