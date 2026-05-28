

namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Decides whether a stream's accumulated event count warrants writing a new snapshot
/// and writes it when needed. Invoked by <see cref="IEventSource"/> on save.
/// </summary>
public interface ISnapshotService
{
    /// <summary>
    /// Inspect <paramref name="eventStreamEntries"/> grouped by stream; for any stream
    /// that has crossed the snapshot threshold, reconstruct + persist a fresh snapshot.
    /// </summary>
    Task AddSnapshotIfNeededAsync(IEnumerable<EventStreamEntry> eventStreamEntries, CancellationToken cancellationToken = default);
}
