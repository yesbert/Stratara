using Stratara.Contracts.Messages;

namespace Stratara.Abstractions.Outbox;

/// <summary>
/// Where an execution model that records every accepted command keeps the bookkeeping of its resume:
/// the command with the aggregate it names and whether it is heavy, how often it has been handed over,
/// how many of its attempts ended in a concurrency conflict, when it was last handed over, the failure of its last
/// attempt, and whether it has been kept for an operator. The resume is bounded only as far as an implementation persists these; an execution model
/// that requires this port fails at start when none is registered, rather than resuming without a bound.
/// </summary>
public interface ICommandIntentStore
{
    /// <summary>Records an accepted command durably, with the aggregate it names and whether it is heavy.</summary>
    /// <param name="intentId">The identity of the recorded command.</param>
    /// <param name="envelope">The serialised command and the session it was dispatched under.</param>
    /// <param name="aggregateId">The aggregate the command names, or <see langword="null"/> for a command that names none.</param>
    /// <param name="heavy">Whether the command declared itself long-running.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the command is durable.</returns>
    Task RecordAsync(Guid intentId, CommandEnvelope envelope, Guid? aggregateId, bool heavy, CancellationToken cancellationToken);

    /// <summary>
    /// Records an accepted command durably, with the aggregate it names, whether it is heavy, and the time its dispatch
    /// began, which orders it among the commands due with it.
    /// </summary>
    /// <param name="intentId">The identity of the recorded command.</param>
    /// <param name="envelope">The serialised command and the session it was dispatched under.</param>
    /// <param name="aggregateId">The aggregate the command names, or <see langword="null"/> for a command that names none.</param>
    /// <param name="heavy">Whether the command declared itself long-running.</param>
    /// <param name="recordedAt">
    /// When the dispatch began, taken before anything was awaited; a later dispatch of the same scope to the same
    /// aggregate carries a later time, however long either takes to be recorded.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the command is durable.</returns>
    /// <remarks>
    /// The default records through the overload without <paramref name="recordedAt"/>, so a store that does not
    /// override it orders commands by the time it stamps them itself.
    /// </remarks>
    Task RecordAsync(Guid intentId, CommandEnvelope envelope, Guid? aggregateId, bool heavy, DateTimeOffset recordedAt, CancellationToken cancellationToken) =>
        RecordAsync(intentId, envelope, aggregateId, heavy, cancellationToken);

    /// <summary>
    /// Returns the recorded commands due for resumption, oldest first, commands recorded at the same time in the order
    /// of their identity: not kept, recorded at or before
    /// <paramref name="handedOverBefore"/>, and not handed over since then.
    /// </summary>
    /// <param name="handedOverBefore">The moment a command's last hand-over must precede for it to be due.</param>
    /// <param name="batchSize">The maximum number of commands to return.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The due commands, or an empty list.</returns>
    Task<IReadOnlyList<RecordedIntent>> GetDueAsync(DateTimeOffset handedOverBefore, int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// Claims a hand-over of a due command: stamps <paramref name="now"/> as its last hand-over and
    /// increments its attempt count, only if it is not kept and its last hand-over is still
    /// <paramref name="expectedLastHandedOverAt"/>.
    /// </summary>
    /// <param name="intentId">The recorded command.</param>
    /// <param name="expectedLastHandedOverAt">The last hand-over read with the command, or <see langword="null"/> for one never handed over.</param>
    /// <param name="now">The time of this hand-over.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> when this call claimed the hand-over.</returns>
    Task<bool> TryClaimAsync(Guid intentId, DateTimeOffset? expectedLastHandedOverAt, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Claims the hand-over of several due commands read together: for each, what <see cref="TryClaimAsync"/> does, and
    /// returns the ones this call claimed.
    /// </summary>
    /// <param name="due">The due commands, as <see cref="GetDueAsync"/> returned them.</param>
    /// <param name="now">The time of this hand-over.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The identities of the commands this call claimed.</returns>
    /// <remarks>
    /// The default claims one command at a time through <see cref="TryClaimAsync"/>, one round trip each. A store that
    /// overrides it claims the batch in a number of round trips that does not grow with the batch, so that a drain
    /// resuming a backlog pays per pass, not per command.
    /// </remarks>
    async Task<IReadOnlyList<Guid>> ClaimAsync(IReadOnlyList<RecordedIntent> due, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(due);
        var claimed = new List<Guid>(due.Count);
        foreach (var intent in due)
        {
            if (await TryClaimAsync(intent.Id, intent.LastHandedOverAt, now, cancellationToken))
            {
                claimed.Add(intent.Id);
            }
        }

        return claimed;
    }

    /// <summary>Renews the hand-over of a command whose handler is running, so it does not become due again while it runs.</summary>
    /// <param name="intentId">The recorded command.</param>
    /// <param name="now">The time of the renewal.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the renewal is durable.</returns>
    Task RenewAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Takes over a resumed hand-over: renews it only if the command is not kept and its last hand-over is still
    /// <paramref name="claimedAt"/>, the stamp of the claim that handed it over, and moves that stamp to
    /// <paramref name="now"/> or, where <paramref name="now"/> is not later, one millisecond past
    /// <paramref name="claimedAt"/>. A second hand-over of the same claim therefore finds the stamp moved, and a
    /// hand-over of a command that completed finds no record.
    /// </summary>
    /// <param name="intentId">The recorded command.</param>
    /// <param name="claimedAt">The last hand-over the claim stamped, as the hand-over carries it.</param>
    /// <param name="now">The time of the renewal.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> when this call took the hand-over over; <see langword="false"/> when another runner
    /// already did or the command is gone, and the hand-over is to be dropped without running the handler.
    /// </returns>
    /// <remarks>
    /// The default renews through <see cref="RenewAsync"/> and returns <see langword="true"/>, so a store that does not
    /// override it runs every hand-over it receives.
    /// </remarks>
    async Task<bool> TryRenewFromAsync(Guid intentId, DateTimeOffset claimedAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await RenewAsync(intentId, now, cancellationToken);
        return true;
    }

    /// <summary>Records the failure of the command's latest attempt.</summary>
    /// <param name="intentId">The recorded command.</param>
    /// <param name="failure">A description of the failure.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the failure is durable.</returns>
    Task RecordFailureAsync(Guid intentId, string failure, CancellationToken cancellationToken);

    /// <summary>
    /// Records that the command's latest attempt ended in a concurrency conflict: records the failure, counts one
    /// conflict, and gives back the attempt the hand-over counted, never below zero — so that the delivery bound
    /// measures failures other than conflicts, and the conflict bound measures conflicts.
    /// </summary>
    /// <param name="intentId">The recorded command.</param>
    /// <param name="failure">A description of the conflict.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the conflict is durable.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The default records the conflict through <see cref="RecordFailureAsync"/>, so a store that does not override it
    /// counts a conflict against the delivery bound like any other failure.
    /// </remarks>
    Task RecordConflictAsync(Guid intentId, string failure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return RecordFailureAsync(intentId, failure, cancellationToken);
    }

    /// <summary>
    /// Gives back the attempt the command's latest hand-over counted, never below zero, because its handler was stopped
    /// with its silo rather than failing.
    /// </summary>
    /// <param name="intentId">The recorded command.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the attempt is given back.</returns>
    /// <remarks>The default gives nothing back, so a store that does not override it counts the stop as an attempt.</remarks>
    Task ReturnAttemptAsync(Guid intentId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Keeps the command for an operator; a kept command is not due until an operator returns it.</summary>
    /// <param name="intentId">The recorded command.</param>
    /// <param name="now">The time it was kept.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the command is kept.</returns>
    Task KeepAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken);
}
