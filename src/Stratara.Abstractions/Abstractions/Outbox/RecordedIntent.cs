using System.Diagnostics.CodeAnalysis;
using Stratara.Contracts.Messages;

namespace Stratara.Abstractions.Outbox;

/// <summary>A recorded command as an <see cref="ICommandIntentStore"/> returns it for resumption.</summary>
/// <param name="Id">The identity of the recorded command.</param>
/// <param name="Envelope">The serialised command and the session it was dispatched under.</param>
/// <param name="AggregateId">The aggregate the command names, or <see langword="null"/> for one that names none.</param>
/// <param name="Heavy">Whether the command declared itself long-running.</param>
/// <param name="AttemptCount">How often it has been handed over since it was recorded or returned.</param>
/// <param name="LastHandedOverAt">When it was last handed over, or <see langword="null"/> if never.</param>
/// <param name="LastFailure">The failure of its last attempt, if one was recorded.</param>
[ExcludeFromCodeCoverage]
public sealed record RecordedIntent(
    Guid Id,
    CommandEnvelope Envelope,
    Guid? AggregateId,
    bool Heavy,
    int AttemptCount,
    DateTimeOffset? LastHandedOverAt,
    string? LastFailure);
