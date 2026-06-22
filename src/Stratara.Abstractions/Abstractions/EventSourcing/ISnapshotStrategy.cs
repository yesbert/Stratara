namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Decides whether the event-sourcing runtime should write a new snapshot for a stream that
/// has just had events appended. This is the configurable replacement for the framework's
/// formerly hard-coded "snapshot every 50 events, always on" policy.
/// </summary>
/// <remarks>
/// <para>
/// The default implementation registered by <c>AddEventSourcing()</c> snapshots a stream once
/// it has advanced by a fixed number of versions since its last snapshot (50 by default).
/// </para>
/// <para>
/// To take over the policy — for example to vary the threshold per aggregate type, or to turn
/// snapshots off entirely — register your own singleton implementation of this interface in DI.
/// The framework resolves the last-registered <see cref="ISnapshotStrategy"/>, so a custom
/// registration wins regardless of whether it runs before or after <c>AddEventSourcing()</c>.
/// </para>
/// </remarks>
public interface ISnapshotStrategy
{
    /// <summary>
    /// Determines whether a snapshot should be written for the stream described by the arguments.
    /// </summary>
    /// <param name="aggregateType">The runtime aggregate type of the stream being evaluated.</param>
    /// <param name="currentVersion">The stream's highest version after the events just appended.</param>
    /// <param name="lastSnapshotVersion">
    /// The version of the most recent existing snapshot for the stream, or <c>0</c> when the stream
    /// has no snapshot yet.
    /// </param>
    /// <returns><c>true</c> to write a fresh snapshot at <paramref name="currentVersion"/>; otherwise <c>false</c>.</returns>
    bool ShouldSnapshot(Type aggregateType, long currentVersion, long lastSnapshotVersion);
}
