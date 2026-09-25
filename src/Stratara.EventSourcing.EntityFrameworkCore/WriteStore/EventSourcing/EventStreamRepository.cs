using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.EventSourcing;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing;

/// <summary>
/// EF Core-backed <see cref="IEventStreamRepository"/> over the <c>event_stream_entry</c>
/// table. Reads are no-tracked; appends use the EF Core change tracker so the surrounding
/// unit of work decides when to flush.
/// </summary>
/// <param name="context">The write-store DbContext that hosts the event-stream table.</param>
internal sealed class EventStreamRepository(IWriteDbContext context) : IEventStreamRepository
{
    /// <inheritdoc/>
    public Task<bool> StreamExistsAsync(Guid streamId, CancellationToken cancellationToken = default) =>
        context.Set<EventStreamEntry>().AnyAsync(e => e.StreamId == streamId, cancellationToken);

    /// <inheritdoc/>
    public Task<EventStreamEntry?> GetFirstOrDefaultAsync(Guid streamId, CancellationToken cancellationToken = default) =>
        context.Set<EventStreamEntry>().AsNoTracking()
            .Where(e => e.StreamId == streamId)
            .OrderBy(e => e.Version)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EventStreamEntry>> GetManyAsync(Guid streamId, long? fromVersion = null, long? toVersion = null,
        CancellationToken cancellationToken = default) =>
        await context.Set<EventStreamEntry>().AsNoTracking()
            .Where(e => e.StreamId == streamId &&
                        (!fromVersion.HasValue || e.Version >= fromVersion) &&
                        (!toVersion.HasValue || e.Version <= toVersion))
            .OrderBy(e => e.Version)
            .ToListAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task<long> GetVersionOrDefaultAsync(Guid streamId, CancellationToken cancellationToken = default) =>
        await context.Set<EventStreamEntry>().AsNoTracking()
            .Where(e => e.StreamId == streamId)
            .OrderByDescending(e => e.Version)
            .Select(e => (long?)e.Version)
            .FirstOrDefaultAsync(cancellationToken) ?? 0L;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EventStreamEntry>> GetUnhashedEventsAsync(int batchSize, DateTimeOffset cutoff,
        CancellationToken cancellationToken = default) =>
        await context.Set<EventStreamEntry>().AsNoTracking()
            .Where(e => e.Hash == null && e.Timestamp <= cutoff)
            .OrderBy(e => e.SequenceNumber)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task<EventStreamEntry?> GetPreviousEventAsync(long sequenceNumber, CancellationToken cancellationToken = default) =>
        await context.Set<EventStreamEntry>().AsNoTracking()
            .Where(x => x.SequenceNumber < sequenceNumber)
            .OrderByDescending(x => x.SequenceNumber)
            .FirstOrDefaultAsync(cancellationToken);


    /// <inheritdoc/>
    public async Task<EventStreamEntry?> GetLastHashedEventAsync(CancellationToken cancellationToken = default) =>
        await context.Set<EventStreamEntry>().AsNoTracking()
            .Where(x => x.Hash != null)
            .OrderByDescending(x => x.SequenceNumber)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EventStreamEntry>> GetManyAfterSequenceAsync(long afterSequenceNumber, int batchSize,
        CancellationToken cancellationToken = default) =>
        await context.Set<EventStreamEntry>().AsNoTracking()
            .Where(e => e.SequenceNumber > afterSequenceNumber)
            .OrderBy(e => e.SequenceNumber)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EventStreamEntry>> GetManyAfterSequenceInStreamOrderAsync(long afterSequenceNumber, int batchSize,
        CancellationToken cancellationToken = default)
    {
        var range = new List<EventStreamEntry>(await GetManyAfterSequenceAsync(afterSequenceNumber, batchSize, cancellationToken));
        if (range.Count == 0)
        {
            return range;
        }

        var end = range[^1].SequenceNumber;
        while (await FindStragglerAsync(afterSequenceNumber, end, cancellationToken) is { } straggler)
        {
            var from = end;
            range.AddRange(await context.Set<EventStreamEntry>().AsNoTracking()
                .Where(e => e.SequenceNumber > from && e.SequenceNumber <= straggler)
                .OrderBy(e => e.SequenceNumber)
                .ToListAsync(cancellationToken));
            end = straggler;
        }

        return InStreamOrder(range);
    }

    /// <summary>
    /// The highest sequence number beyond <paramref name="end"/> of an entry whose stream appears in the range at a
    /// higher version, or <see langword="null"/> where no stream of the range continues below its top beyond it.
    /// </summary>
    private Task<long?> FindStragglerAsync(long afterSequenceNumber, long end, CancellationToken cancellationToken)
    {
        var entries = context.Set<EventStreamEntry>().AsNoTracking();
        var tops = entries
            .Where(e => e.SequenceNumber > afterSequenceNumber && e.SequenceNumber <= end)
            .GroupBy(e => new { e.BucketId, e.StreamId })
            .Select(g => new { g.Key.BucketId, g.Key.StreamId, Top = g.Max(e => e.Version) });

        return entries
            .Where(e => e.SequenceNumber > end)
            .Join(tops,
                e => new { e.BucketId, e.StreamId },
                t => new { t.BucketId, t.StreamId },
                (e, t) => new { e.SequenceNumber, e.Version, t.Top })
            .Where(x => x.Version < x.Top)
            .MaxAsync(x => (long?)x.SequenceNumber, cancellationToken);
    }

    /// <summary>
    /// Gives each stream's places in the range, in sequence order, to its entries in version order: a slot takes the
    /// lowest version of its stream not yet placed, so the streams keep the interleaving the sequence gives them.
    /// </summary>
    private static List<EventStreamEntry> InStreamOrder(List<EventStreamEntry> range)
    {
        var versions = range
            .GroupBy(e => (e.BucketId, e.StreamId))
            .ToDictionary(g => g.Key, g => new Queue<EventStreamEntry>(g.OrderBy(e => e.Version)));
        return [.. range.Select(slot => versions[(slot.BucketId, slot.StreamId)].Dequeue())];
    }

    /// <inheritdoc/>
    public async Task<long> GetMaxSequenceNumberAsync(CancellationToken cancellationToken = default) =>
        await context.Set<EventStreamEntry>().AsNoTracking()
            .OrderByDescending(e => e.SequenceNumber)
            .Select(e => (long?)e.SequenceNumber)
            .FirstOrDefaultAsync(cancellationToken) ?? 0L;

    /// <inheritdoc/>
    public async Task AddRangeAsync(IReadOnlyList<EventStreamEntry> eventEntries, CancellationToken cancellationToken = default)
    {
        await context.Set<EventStreamEntry>().AddRangeAsync(eventEntries, cancellationToken);
    }

    /// <inheritdoc/>
    public void UpdateRange(IReadOnlyList<EventStreamEntry> eventEntries)
    {
        context.Set<EventStreamEntry>().UpdateRange(eventEntries);
    }
}
