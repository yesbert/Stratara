namespace Stratara.Abstractions.Messaging;

/// <summary>Why a handler did not take a message.</summary>
public enum MessageFailureKind
{
    /// <summary>The handler reported a concurrency conflict; a later delivery is expected to succeed.</summary>
    Conflict = 0,

    /// <summary>The handler threw anything else.</summary>
    Failure = 1,
}

/// <summary>What the transport does with a message its handler did not take.</summary>
public enum MessageDisposition
{
    /// <summary>Hand the message back to the broker for another delivery.</summary>
    Redeliver = 0,

    /// <summary>Move the message to the subscription's dead-letter destination.</summary>
    DeadLetter = 1,
}

/// <summary>
/// Decides, from the number of times a message has been delivered and why its handler did not take
/// it, whether the transport redelivers it or dead-letters it. Every transport the framework ships
/// applies this policy from the delivery count its broker supplies, so the bounds in
/// <see cref="MessageRetryOptions"/> mean the same number of handler runs on each.
/// </summary>
/// <param name="options">The bounds to apply.</param>
public sealed class MessageRetryPolicy(MessageRetryOptions options)
{
    /// <summary>The dead-letter reason recorded for a message that exhausted <see cref="MessageRetryOptions.MaxConflictRequeues"/>.</summary>
    public const string ConflictReason = "conflict";

    /// <summary>The dead-letter reason recorded for a message that exhausted <see cref="MessageRetryOptions.MaxDeliveryAttempts"/>.</summary>
    public const string FailureReason = "failure";

    private readonly int _maxDeliveryAttempts = options.MaxDeliveryAttempts;
    private readonly int _maxConflictRequeues = options.MaxConflictRequeues;

    /// <summary>
    /// The delivery count at which a broker's own limit may take over as a backstop: one above the
    /// larger of the two bounds, so that the framework's decision always comes first.
    /// </summary>
    public int BrokerDeliveryLimit => Math.Max(_maxDeliveryAttempts, _maxConflictRequeues) + 1;

    /// <summary>
    /// Decides what happens to a message after its handler did not take it.
    /// </summary>
    /// <param name="deliveryAttempt">How many times the message has been delivered, counting the one that just failed; 1 on the first delivery.</param>
    /// <param name="kind">Why the handler did not take it.</param>
    /// <returns>Whether to redeliver or to dead-letter.</returns>
    public MessageDisposition Decide(int deliveryAttempt, MessageFailureKind kind) => kind switch
    {
        MessageFailureKind.Conflict when deliveryAttempt > _maxConflictRequeues => MessageDisposition.DeadLetter,
        MessageFailureKind.Failure when deliveryAttempt >= _maxDeliveryAttempts => MessageDisposition.DeadLetter,
        _ => MessageDisposition.Redeliver,
    };

    /// <summary>The dead-letter reason recorded for <paramref name="kind"/>.</summary>
    /// <param name="kind">Why the handler did not take the message.</param>
    /// <returns><see cref="ConflictReason"/> or <see cref="FailureReason"/>.</returns>
    public static string ReasonFor(MessageFailureKind kind) =>
        kind == MessageFailureKind.Conflict ? ConflictReason : FailureReason;
}
