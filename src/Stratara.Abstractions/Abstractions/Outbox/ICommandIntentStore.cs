using Stratara.Contracts.Messages;

namespace Stratara.Abstractions.Outbox;

/// <summary>
/// Where an execution model that records every accepted command keeps the bookkeeping of its resume:
/// the command with the aggregate it names and whether it is heavy, how often it has been handed over,
/// when it was last handed over, the failure of its last attempt, and whether it has been kept for an
/// operator. The resume is bounded only as far as an implementation persists these; an execution model
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
    /// Returns the recorded commands due for resumption, oldest first: not kept, recorded at or before
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

    /// <summary>Records the failure of the command's latest attempt.</summary>
    /// <param name="intentId">The recorded command.</param>
    /// <param name="failure">A description of the failure.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the failure is durable.</returns>
    Task RecordFailureAsync(Guid intentId, string failure, CancellationToken cancellationToken);

    /// <summary>Keeps the command for an operator; a kept command is not due until an operator returns it.</summary>
    /// <param name="intentId">The recorded command.</param>
    /// <param name="now">The time it was kept.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the command is kept.</returns>
    Task KeepAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken);
}
