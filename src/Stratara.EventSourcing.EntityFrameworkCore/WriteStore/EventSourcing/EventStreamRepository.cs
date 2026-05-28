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
