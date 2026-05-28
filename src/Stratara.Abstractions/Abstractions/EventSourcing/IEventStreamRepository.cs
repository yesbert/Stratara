

namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Low-level repository over the <c>event_stream_entry</c> table. Implementations live
/// in the EF Core write-store package; consumers typically go through
/// <see cref="IEventSource"/> instead of using this directly.
/// </summary>
public interface IEventStreamRepository
{
    /// <summary>Returns <c>true</c> if the stream has at least one entry.</summary>
    Task<bool> StreamExistsAsync(Guid streamId, CancellationToken cancellationToken = default);

    /// <summary>Returns the first entry of the stream — typically the creation event.</summary>
    Task<EventStreamEntry?> GetFirstOrDefaultAsync(Guid streamId, CancellationToken cancellationToken = default);

    /// <summary>Returns entries of the stream filtered by version range, ascending by version.</summary>
    Task<IReadOnlyList<EventStreamEntry>> GetManyAsync(Guid streamId, long? fromVersion = null, long? toVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the head version of the stream, or <c>0</c> if it does not exist.</summary>
    Task<long> GetVersionOrDefaultAsync(Guid streamId, CancellationToken cancellationToken = default);

    /// <summary>Returns up to <paramref name="batchSize"/> entries that have not yet been hashed and are older than <paramref name="cutoff"/>.</summary>
    Task<IReadOnlyList<EventStreamEntry>> GetUnhashedEventsAsync(int batchSize, DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    /// <summary>Returns the entry immediately preceding <paramref name="sequenceNumber"/> in stream order.</summary>
    Task<EventStreamEntry?> GetPreviousEventAsync(long sequenceNumber, CancellationToken cancellationToken = default);

    /// <summary>Returns the most recent entry whose hash has been computed.</summary>
    Task<EventStreamEntry?> GetLastHashedEventAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns up to <paramref name="batchSize"/> entries with a sequence number greater than <paramref name="afterSequenceNumber"/>.</summary>
    Task<IReadOnlyList<EventStreamEntry>> GetManyAfterSequenceAsync(long afterSequenceNumber, int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the maximum sequence number across all streams.</summary>
    Task<long> GetMaxSequenceNumberAsync(CancellationToken cancellationToken = default);

    /// <summary>Appends entries to the underlying DbContext. Caller is responsible for the transactional save.</summary>
    Task AddRangeAsync(IReadOnlyList<EventStreamEntry> eventEntries, CancellationToken cancellationToken = default);

    /// <summary>Updates entries already tracked by the underlying DbContext.</summary>
    void UpdateRange(IReadOnlyList<EventStreamEntry> eventEntries);
}
