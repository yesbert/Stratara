using System.Diagnostics.CodeAnalysis;
using Stratara.Contracts.Messages;

namespace Stratara.Abstractions.Outbox;

/// <summary>A recorded command as an <see cref="ICommandIntentStore"/> returns it for resumption.</summary>
/// <param name="Id">The identity of the recorded command.</param>
/// <param name="Envelope">The serialised command and the session it was dispatched under.</param>
/// <param name="AggregateId">The aggregate the command names, or <see langword="null"/> for one that names none.</param>
/// <param name="Heavy">Whether the command declared itself long-running.</param>
/// <param name="AttemptCount">
/// How often it has been handed over since it was recorded or returned — the hand-over of its dispatch counted when it
/// was recorded, by a store that counts it — less the attempts given back for a concurrency conflict or a stop.
/// </param>
/// <param name="LastHandedOverAt">When it was last handed over, or <see langword="null"/> if never.</param>
/// <param name="LastFailure">The failure of its last attempt, if one was recorded.</param>
/// <param name="RecordedByTheExecutionModel">
/// Whether the execution model wrote this record itself, rather than it being a command the bus outbox stored — a
/// store may hold both during a rolling adoption, and only the model's own records carry the routing beside the
/// envelope. Defaults to <see langword="true"/>.
/// </param>
/// <param name="ConflictCount">How many of its attempts since it was recorded or returned ended in a concurrency conflict. Defaults to 0.</param>
[ExcludeFromCodeCoverage]
public sealed record RecordedIntent(
    Guid Id,
    CommandEnvelope Envelope,
    Guid? AggregateId,
    bool Heavy,
    int AttemptCount,
    DateTimeOffset? LastHandedOverAt,
    string? LastFailure,
    bool RecordedByTheExecutionModel = true,
    int ConflictCount = 0);
