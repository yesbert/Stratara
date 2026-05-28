using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing;

/// <summary>
/// EF Core-backed <see cref="IEventChainRepository"/> over the event-chain anchor table. Used
/// by the event-stream hashing worker to read the last sequence number per bucket and append
/// new anchors as the chain advances.
/// </summary>
/// <param name="context">The write-store DbContext that hosts the anchor table.</param>
internal sealed class EventChainRepository(IWriteDbContext context) : IEventChainRepository
{
    /// <inheritdoc/>
    public async Task<long> GetLastSequenceNumberOrDefaultAsync(int[] bucketIds, CancellationToken cancellationToken = default)
    {
        var query = context.Set<EventChainAnchor>().AsNoTracking();

        if (bucketIds.Length > 0)
        {
            query = query.Where(e => bucketIds.Contains(e.BucketId));
        }

        return await query
            .OrderByDescending(e => e.SequenceNumber)
            .Select(e => (long?)e.SequenceNumber)
            .FirstOrDefaultAsync(cancellationToken) ?? 0L;
    }

    /// <inheritdoc/>
    public async Task AddAnchorAsync(EventChainAnchor anchor, CancellationToken cancellationToken)
    {
        await context.Set<EventChainAnchor>().AddAsync(anchor, cancellationToken);
    }
}
