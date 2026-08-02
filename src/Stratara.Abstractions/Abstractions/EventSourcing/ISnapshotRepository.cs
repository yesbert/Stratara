

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

    /// <summary>Append a snapshot. Caller is responsible for the transactional save.</summary>
    Task AddAsync(Snapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the version of the most recent snapshot for the stream of the aggregate type
    /// identified by <paramref name="aggregateTypeName"/> (version-independent match), or <c>0</c>.
    /// </summary>
    /// <param name="streamId">The stream to inspect.</param>
    /// <param name="aggregateTypeName">The (assembly-)qualified name of the aggregate type to match.</param>
    /// <param name="cancellationToken">Propagated to the query.</param>
    Task<long> GetLatestVersionOrDefaultAsync(Guid streamId, string aggregateTypeName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the latest snapshot for <paramref name="streamId"/> with a version no
    /// greater than <paramref name="toVersion"/>, or <c>null</c> if none exists.
    /// </summary>
    /// <remarks>
    /// This type-less lookup can return a snapshot of a different aggregate type that shares the
    /// stream id, which is then deserialized into the requested type and returns corrupt/default
    /// state. Use the overload that takes an <c>aggregateTypeName</c>.
    /// </remarks>
    /// <param name="streamId">The stream to load a snapshot for.</param>
    /// <param name="toVersion">Upper version bound (inclusive), or <c>null</c> for the latest.</param>
    /// <param name="cancellationToken">Propagated to the query.</param>
    [Obsolete("Use GetAsync(streamId, aggregateTypeName, toVersion, cancellationToken). The type-less lookup can return a snapshot of a different aggregate type sharing the stream id and corrupt the rehydrated state.")]
    Task<Snapshot?> GetAsync(Guid streamId, long? toVersion = null, CancellationToken cancellationToken = default);

    /// <summary>Returns the version of the most recent snapshot for the stream, or <c>0</c>.</summary>
    /// <remarks>
    /// This type-less lookup ignores the aggregate type and can report the version of a foreign-type
    /// snapshot on a shared stream. Use the overload that takes an <c>aggregateTypeName</c>.
    /// </remarks>
    /// <param name="streamId">The stream to inspect.</param>
    /// <param name="cancellationToken">Propagated to the query.</param>
    [Obsolete("Use GetLatestVersionOrDefaultAsync(streamId, aggregateTypeName, cancellationToken). The type-less lookup can report the version of a foreign-type snapshot on a shared stream.")]
    Task<long> GetLatestVersionOrDefaultAsync(Guid streamId, CancellationToken cancellationToken = default);
}
