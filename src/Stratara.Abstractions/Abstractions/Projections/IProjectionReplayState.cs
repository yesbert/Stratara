namespace Stratara.Abstractions.Projections;

/// <summary>
/// In-memory coordination handle for the projection-replay state-machine. Workers query
/// this to decide whether they are currently running a replay; admin endpoints flip it
/// via <see cref="Activate"/> / <see cref="Deactivate"/>.
/// </summary>
/// <remarks>
/// Implementations should be process-singleton and thread-safe; the replay handshake
/// crosses worker boundaries via the <see cref="SubscribeToReplayRequestAsync"/> +
/// <see cref="RequestReplay"/> channel.
/// </remarks>
public interface IProjectionReplayState
{
    /// <summary><c>true</c> while a replay is in progress; <c>false</c> in steady-state.</summary>
    bool IsReplayActive { get; }

    /// <summary>Mark the replay as started.</summary>
    void Activate();

    /// <summary>Mark the replay as completed (success or graceful stop).</summary>
    void Deactivate();

    /// <summary>Mark the current replay as failed and record <paramref name="errorMessage"/>.</summary>
    void SetFailed(string errorMessage);

    /// <summary>Register a callback fired whenever <see cref="RequestReplay"/> is invoked.</summary>
    Task SubscribeToReplayRequestAsync(Func<Task> onReplayRequested, CancellationToken cancellationToken = default);

    /// <summary>Signal that a replay should start — fires every subscribed callback.</summary>
    void RequestReplay();

    /// <summary>Update the replay progress counters.</summary>
    void SetProgress(long processedEvents, long totalEvents);

    /// <summary>Snapshot the current replay progress.</summary>
    ReplayProgress GetProgress();
}

/// <summary>
/// Snapshot of replay progress at a point in time. Returned by
/// <see cref="IProjectionReplayState.GetProgress"/> for status dashboards.
/// </summary>
/// <param name="IsActive">Whether a replay is currently running.</param>
/// <param name="ProcessedEvents">Number of events already processed.</param>
/// <param name="TotalEvents">Total number of events to process for this replay.</param>
/// <param name="Percentage">Convenience integer percentage in <c>[0, 100]</c>.</param>
/// <param name="ErrorMessage">Last error message if the replay failed; <c>null</c> otherwise.</param>
public sealed record ReplayProgress(bool IsActive, long ProcessedEvents, long TotalEvents, int Percentage, string? ErrorMessage = null);

/// <summary>
/// Truncates every projection view as part of a replay reset. Implementations typically
/// execute a single transaction with <c>TRUNCATE ... CASCADE</c> across the read-store
/// schema.
/// </summary>
public interface IProjectionViewTruncator
{
    /// <summary>Truncate every projection view managed by the host.</summary>
    Task TruncateAllAsync(CancellationToken cancellationToken = default);
}
