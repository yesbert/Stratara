

namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Decides whether a stream's accumulated event count warrants writing a new snapshot
/// and writes it when needed. Invoked by <see cref="IEventSource"/> on save.
/// </summary>
/// <remarks>
/// The event source calls it once the batch is committed and published, so the entries it receives are
/// recorded, and a snapshot built from the committed stream captures nothing a failed save wrote. A
/// failure it throws is logged by the event source and does not fail the save: a snapshot is a cache.
/// </remarks>
public interface ISnapshotService
{
    /// <summary>
    /// Inspect <paramref name="eventStreamEntries"/> grouped by stream; for any stream
    /// that has crossed the snapshot threshold, reconstruct + persist a fresh snapshot.
    /// </summary>
    Task AddSnapshotIfNeededAsync(IEnumerable<EventStreamEntry> eventStreamEntries, CancellationToken cancellationToken = default);
}
