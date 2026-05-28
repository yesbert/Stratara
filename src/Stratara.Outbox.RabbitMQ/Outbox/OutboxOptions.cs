using System.Diagnostics.CodeAnalysis;

namespace Stratara.Outbox.RabbitMQ.Outbox;

/// <summary>
/// Options that control how <see cref="OutboxWorker"/> drains the outbox table.
/// Bound from the <c>Outbox</c> configuration section (see <see cref="SectionName"/>).
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class OutboxOptions
{
    /// <summary>Configuration section name (<c>"Outbox"</c>) used to bind these options.</summary>
    public const string SectionName = "Outbox";

    /// <summary>
    /// Interval (in seconds) between two outbox drain attempts. The worker polls the outbox table,
    /// publishes any unsent entries, sleeps for this interval, and repeats. Defaults to 30 seconds.
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of outbox entries fetched per drain iteration. Defaults to 10 000.
    /// The worker keeps fetching batches of this size until the table is drained, so this value
    /// caps per-query memory pressure rather than total throughput.
    /// </summary>
    public int BatchSize { get; set; } = 10_000;

    /// <summary>
    /// Lease (in seconds) requested when the worker acquires the outbox-drain lock. Should be
    /// comfortably longer than the worst-case drain duration (batch size × per-publish latency)
    /// so the lock does not expire mid-cycle. Defaults to 60 seconds. Has no effect when the
    /// no-op <c>NullOutboxLock</c> is registered.
    /// </summary>
    public int LockLeaseSeconds { get; set; } = 60;
}
