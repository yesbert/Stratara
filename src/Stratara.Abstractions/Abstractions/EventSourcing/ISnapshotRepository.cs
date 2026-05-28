

namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Repository over the <c>snapshot</c> table — periodic state captures that let the
/// aggregation service skip replaying every event from the start of a stream.
/// </summary>
public interface ISnapshotRepository
{
    /// <summary>
    /// Returns the latest snapshot for <paramref name="streamId"/> with a version no
    /// greater than <paramref name="toVersion"/>, or <c>null</c> if none exists.
    /// </summary>
    Task<Snapshot?> GetAsync(Guid streamId, long? toVersion = null, CancellationToken cancellationToken = default);

    /// <summary>Append a snapshot. Caller is responsible for the transactional save.</summary>
    Task AddAsync(Snapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>Returns the version of the most recent snapshot for the stream, or <c>0</c>.</summary>
    Task<long> GetLatestVersionOrDefaultAsync(Guid streamId, CancellationToken cancellationToken = default);
}
