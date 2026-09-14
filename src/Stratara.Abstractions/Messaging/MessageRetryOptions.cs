using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.Messaging;

/// <summary>
/// How often a durable subscription redelivers a message its handler cannot take before the
/// message is moved to the subscription's dead-letter destination. The same bounds apply on every
/// transport the framework ships.
/// </summary>
/// <remarks>
/// <para>
/// Bind from configuration via section <see cref="SectionName"/> (<c>"MessageRetry"</c>). Both
/// transport registrations bind the same section, so a host that switches broker keeps its bounds.
/// </para>
/// <para>
/// A handler failure and a concurrency conflict are bounded separately: a failure is usually
/// deterministic and three identical attempts are enough to tell, while a conflict is expected to
/// resolve on a later delivery and may take many. Neither bound is a delay — a redelivery is
/// immediate on both transports.
/// </para>
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class MessageRetryOptions
{
    /// <summary>Configuration section name (<c>"MessageRetry"</c>) used to bind these options.</summary>
    public const string SectionName = "MessageRetry";

    /// <summary>
    /// How many times a message is handed to its handler before a failure other than a concurrency
    /// conflict moves it to the dead-letter destination. Defaults to 3; the minimum is 1, which
    /// dead-letters on the first failure.
    /// </summary>
    public int MaxDeliveryAttempts { get; set; } = 3;

    /// <summary>
    /// How many times a message that reports a concurrency conflict is redelivered before it is
    /// moved to the dead-letter destination. Defaults to 100; the minimum is 1.
    /// </summary>
    public int MaxConflictRequeues { get; set; } = 100;
}
