namespace Stratara.Projections.Abstractions;

/// <summary>
/// Keeps the read models a replay is about to empty, so a replay that fails — or whose host stops — leaves them as
/// they were before it began instead of empty or half rebuilt.
/// </summary>
/// <remarks>
/// <para>
/// The replay worker uses an implementation when one is registered and replays exactly as without it otherwise. It
/// preserves before it empties anything, restores after a failure that persisted through the batch retries, and
/// discards after a replay that succeeded; a replay whose host stopped leaves the preserved state, and the next host
/// that starts restores it through <see cref="RestoreAbandonedAsync"/>.
/// </para>
/// <para>
/// What is preserved must be one consistent state: the read models together with what decides how they continue —
/// the positions of projections that read the store and each projection's record of forgotten tenants — so the
/// restored state is one that existed. <c>AddReadModelRestore&lt;TReadContext&gt;()</c> registers the PostgreSQL
/// implementation.
/// </para>
/// </remarks>
public interface IReadModelPreservation
{
    /// <summary>
    /// Preserves the read models for the replay <paramref name="replayId"/>, before anything is emptied. Where a state
    /// preserved for a replay that did not finish is still kept, that state is kept and handed to this replay instead:
    /// it is the last complete one, and what is in place is partial.
    /// </summary>
    /// <param name="replayId">The identity of the request the replay runs.</param>
    /// <param name="cancellationToken">Cancels the preservation; nothing has been emptied when it is cancelled.</param>
    /// <returns>A task that completes once the state is preserved.</returns>
    /// <exception cref="InvalidOperationException">
    /// The read models cannot be preserved so that they could be restored — for example because a table outside them
    /// references one of them. Nothing has been emptied.
    /// </exception>
    Task PreserveAsync(Guid replayId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the state preserved for <paramref name="replayId"/> back in place of what the replay rebuilt, as one
    /// step readers observe entirely or not at all, and drops it.
    /// </summary>
    /// <param name="replayId">The identity of the request the failed replay ran.</param>
    /// <param name="cancellationToken">Cancels the restoration, which then leaves the read models as they were.</param>
    /// <returns><see langword="true"/> when the state was restored; <see langword="false"/> when none is kept for the replay.</returns>
    /// <exception cref="InvalidOperationException">
    /// The preserved state no longer fits the read models — a schema change ran in between. The preserved state is
    /// kept for the operator.
    /// </exception>
    Task<bool> RestoreAsync(Guid replayId, CancellationToken cancellationToken = default);

    /// <summary>Drops the state preserved for <paramref name="replayId"/> after the replay succeeded.</summary>
    /// <param name="replayId">The identity of the request the replay ran.</param>
    /// <param name="cancellationToken">Cancels the discard.</param>
    /// <returns>A task that completes once the preserved state is dropped.</returns>
    Task DiscardAsync(Guid replayId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a preserved state that no running replay owns — left by a replay whose host stopped — and drops one
    /// whose replay succeeded but could not drop it. Hosts that start at once restore it once.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>
    /// What was found: nothing kept, a state restored, a state dropped, or a state a replay marked active still owns —
    /// which the caller checks again later, because a host that restarted within its replay's lease finds that replay
    /// still marked active.
    /// </returns>
    Task<AbandonedPreservation> RestoreAbandonedAsync(CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IReadModelPreservation.RestoreAbandonedAsync"/> found.</summary>
public enum AbandonedPreservation
{
    /// <summary>No preserved state is kept.</summary>
    NoneKept,

    /// <summary>A state left by a replay that did not finish was written back.</summary>
    Restored,

    /// <summary>A state left by a replay that succeeded was dropped.</summary>
    Discarded,

    /// <summary>A state is kept, and a replay is marked active; check again once its marking has ended or lapsed.</summary>
    StillOwned,
}
