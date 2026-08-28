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
    /// Maximum number of outbox entries fetched per drain cycle and entry kind. Defaults to 10 000.
    /// A cycle takes one batch of commands and one batch of event bundles and ends; entries the bus
    /// did not accept stay in the table and are retried on the next interval. Together with
    /// <see cref="PollingIntervalSeconds"/> this value therefore sets the drain rate — 20 000 entries
    /// a minute with the defaults. Raise it, or shorten the polling interval, to work off a large
    /// backlog faster.
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
