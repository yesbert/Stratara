

namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Repository over the <c>snapshot</c> table — periodic state captures that let the
/// aggregation service skip replaying every event from the start of a stream.
/// </summary>
public interface ISnapshotRepository
{
    /// <summary>
    /// Returns the latest snapshot for <paramref name="streamId"/> of the aggregate type identified
    /// by <paramref name="aggregateTypeName"/> with a version no greater than
    /// <paramref name="toVersion"/>, or <c>null</c> if none exists.
    /// </summary>
    /// <remarks>
    /// The type match is version-independent: assembly version / culture / public-key-token segments
    /// are ignored so an assembly rev-bump does not invalidate existing snapshots. Scoping the read to
    /// the aggregate type is required — two aggregate types keyed on the same stream id must never
    /// rehydrate from each other's snapshot.
    /// </remarks>
    /// <param name="streamId">The stream to load a snapshot for.</param>
    /// <param name="aggregateTypeName">The (assembly-)qualified name of the aggregate type to match.</param>
    /// <param name="toVersion">Upper version bound (inclusive), or <c>null</c> for the latest.</param>
    /// <param name="cancellationToken">Propagated to the query.</param>
    Task<Snapshot?> GetAsync(Guid streamId, string aggregateTypeName, long? toVersion = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the version of the most recent snapshot for the stream of the aggregate type
    /// identified by <paramref name="aggregateTypeName"/> (version-independent match), or <c>0</c>.
    /// </summary>
    /// <param name="streamId">The stream to inspect.</param>
    /// <param name="aggregateTypeName">The (assembly-)qualified name of the aggregate type to match.</param>
    /// <param name="cancellationToken">Propagated to the query.</param>
    Task<long> GetLatestVersionOrDefaultAsync(Guid streamId, string aggregateTypeName, CancellationToken cancellationToken = default);

    /// <summary>Append a snapshot. Caller is responsible for the transactional save.</summary>
    Task AddAsync(Snapshot snapshot, CancellationToken cancellationToken = default);
}
