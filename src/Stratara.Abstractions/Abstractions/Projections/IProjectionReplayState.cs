namespace Stratara.Abstractions.Projections;

/// <summary>
/// In-memory coordination handle for the projection-replay state-machine. Workers query
/// this to decide whether they are currently running a replay; admin endpoints flip it
/// via <see cref="Activate"/> / <see cref="Deactivate"/>.
/// </summary>
/// <remarks>
/// Implementations should be process-singleton and thread-safe; the replay handshake
/// crosses worker boundaries via the <see cref="SubscribeToReplayRequestAsync(Func{Guid, Task}, CancellationToken)"/> +
/// <see cref="RequestReplay(Guid)"/> channel. <see cref="IsReplayActive"/> is read on every dispatch,
/// every publication and every catch-up, so an implementation answers it from memory and never
/// waits on a shared store for it; a marking shared between hosts is learned asynchronously, within
/// a bounded period.
/// </remarks>
public interface IProjectionReplayState
{
    /// <summary>
    /// <c>true</c> while a replay is in progress; <c>false</c> in steady-state. Answered from the
    /// host's memory: a change made through this instance is seen at once, a change made in another
    /// host sharing the marking within the refresh period the host configures, and a host that cannot
    /// reach the shared store keeps its last answer.
    /// </summary>
    bool IsReplayActive { get; }

    /// <summary>Mark the replay as started.</summary>
    void Activate();

    /// <summary>Mark the replay as completed (success or graceful stop).</summary>
    void Deactivate();

    /// <summary>Mark the current replay as failed and record <paramref name="errorMessage"/>.</summary>
    void SetFailed(string errorMessage);

    /// <summary>Register a callback fired whenever a replay is requested.</summary>
    Task SubscribeToReplayRequestAsync(Func<Task> onReplayRequested, CancellationToken cancellationToken = default);

    /// <summary>Signal that a replay should start — fires every subscribed callback.</summary>
    /// <remarks>The request is given a new identity; use <see cref="RequestReplay(Guid)"/> to choose it.</remarks>
    void RequestReplay();

    /// <summary>Update the replay progress counters.</summary>
    void SetProgress(long processedEvents, long totalEvents);

    /// <summary>Snapshot the current replay progress.</summary>
    ReplayProgress GetProgress();

    /// <summary>
    /// Signal that a replay should start, under an identity the requester chose — fires every subscribed
    /// callback with <paramref name="requestId"/>.
    /// </summary>
    /// <remarks>
    /// The running replay's <see cref="ReplayProgress.RequestId"/> and, once it has ended,
    /// <see cref="ReplayOutcome.RequestId"/> name the request, so a requester that polls can tell its own
    /// replay from an older one. Among the hosts that share the coordination state, one request starts at
    /// most one replay. The default implementation forwards to <see cref="RequestReplay()"/>, which loses
    /// the identity.
    /// </remarks>
    /// <param name="requestId">The identity of the request; a new <see cref="Guid"/> per request.</param>
    void RequestReplay(Guid requestId) => RequestReplay();

    /// <summary>Register a callback fired with the request's identity whenever a replay is requested.</summary>
    /// <remarks>
    /// The default implementation subscribes through
    /// <see cref="SubscribeToReplayRequestAsync(Func{Task}, CancellationToken)"/> and passes
    /// <see cref="Guid.Empty"/>, because that member carries no identity.
    /// </remarks>
    /// <param name="onReplayRequested">The callback, given the identity of the request.</param>
    /// <param name="cancellationToken">Cancels the subscription's establishment.</param>
    /// <returns>A task that completes once the callback is registered.</returns>
    Task SubscribeToReplayRequestAsync(Func<Guid, Task> onReplayRequested, CancellationToken cancellationToken = default) =>
        SubscribeToReplayRequestAsync(() => onReplayRequested(Guid.Empty), cancellationToken);

    /// <summary>
    /// Claim the request <paramref name="requestId"/> and mark its replay as started, unless another host
    /// has claimed it already or another replay is active.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> — and starts nothing — when the request was claimed before, so a
    /// request seen by several hosts, or seen again after its replay ended, runs once; and when another
    /// replay is active, so a second replay never empties what a running one is rebuilding. The default
    /// implementation refuses while <see cref="IsReplayActive"/> and otherwise calls
    /// <see cref="Activate"/>; it cannot tell one request from another.
    /// </remarks>
    /// <param name="requestId">The identity of the request to run.</param>
    /// <returns><see langword="true"/> when this caller is to run the replay.</returns>
    bool TryActivate(Guid requestId)
    {
        if (IsReplayActive)
        {
            return false;
        }

        Activate();
        return true;
    }

    /// <summary>
    /// Mark the replay of <see cref="ReplayCompletion.RequestId"/> as ended and keep its outcome, readable through
    /// <see cref="ReplayProgress.LastReplay"/> until the next replay ends.
    /// </summary>
    /// <remarks>
    /// The marking is ended only while it still belongs to that request: a replay that outlived its lease, while
    /// another request started, records its outcome and leaves the other replay's marking alone. The default
    /// implementation calls <see cref="SetFailed"/> for a failure and <see cref="Deactivate"/> otherwise, and keeps
    /// no outcome.
    /// </remarks>
    /// <param name="completion">How the replay ended.</param>
    void Complete(ReplayCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (completion.Result == ReplayResult.Failed)
        {
            SetFailed(completion.ErrorMessage ?? string.Empty);
        }
        else
        {
            Deactivate();
        }
    }
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
public sealed record ReplayProgress(bool IsActive, long ProcessedEvents, long TotalEvents, int Percentage, string? ErrorMessage = null)
{
    /// <summary>The identity of the request the running replay runs; <see langword="null"/> while none runs.</summary>
    public Guid? RequestId { get; init; }

    /// <summary>
    /// The outcome of the last replay that ended, kept until the next one ends; <see langword="null"/> before
    /// any replay has ended. A reader that polls can tell from it a replay that finished between two polls
    /// from one that never ran.
    /// </summary>
    public ReplayOutcome? LastReplay { get; init; }
}

/// <summary>How a replay ended.</summary>
public enum ReplayResult
{
    /// <summary>The replay applied the whole event stream.</summary>
    Succeeded,

    /// <summary>The replay ended on a failure that persisted through its retries.</summary>
    Failed,

    /// <summary>The replay ended because its host stopped; not a failure.</summary>
    Interrupted,
}

/// <summary>How a replay ended, as the replaying worker reports it to <see cref="IProjectionReplayState.Complete"/>.</summary>
/// <param name="RequestId">The identity of the request the replay ran, as given to <see cref="IProjectionReplayState.TryActivate"/>.</param>
/// <param name="Result">How the replay ended.</param>
/// <param name="ReplayedEvents">How many events the replay had applied when it ended.</param>
/// <param name="ErrorMessage">The failure's message, for <see cref="ReplayResult.Failed"/>; <see langword="null"/> otherwise.</param>
public sealed record ReplayCompletion(Guid RequestId, ReplayResult Result, long ReplayedEvents, string? ErrorMessage = null)
{
    /// <summary>Whether the read models the replay emptied were restored to their state before it began.</summary>
    public bool ReadModelsRestored { get; init; }
}

/// <summary>The outcome of a replay that has ended.</summary>
/// <param name="RequestId">The identity of the request the replay ran.</param>
/// <param name="StartedAt">When the replay started, by the replaying host's clock.</param>
/// <param name="EndedAt">When the replay ended, by the replaying host's clock.</param>
/// <param name="ReplayedEvents">How many events the replay applied.</param>
/// <param name="Result">How the replay ended.</param>
/// <param name="ErrorMessage">The failure's message, for <see cref="ReplayResult.Failed"/>; <see langword="null"/> otherwise.</param>
public sealed record ReplayOutcome(
    Guid RequestId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long ReplayedEvents,
    ReplayResult Result,
    string? ErrorMessage = null)
{
    /// <summary>
    /// Whether the read models were restored to their state before the replay began — after a failure, on a host that
    /// keeps the read models a replay empties.
    /// </summary>
    public bool ReadModelsRestored { get; init; }
}

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
