using Stratara.Abstractions.Timers;

namespace Stratara.Orleans.Timers;

/// <summary>Settings for <see cref="IDurableTimers"/>.</summary>
public sealed class DurableTimerOptions
{
    /// <summary>
    /// How long after a failed or early firing the timer is looked at again. Also the period the
    /// underlying reminder is registered with, so it cannot be shorter than the cluster's minimum
    /// reminder period.
    /// </summary>
    public TimeSpan RetryPeriod { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How much earlier than its due time a tick still fires. The due time is computed on the host that
    /// registered the timer and the tick runs on a silo with a clock of its own; without a tolerance a tick
    /// that arrives a moment early waits a whole <see cref="RetryPeriod"/>. A timer that fires within the
    /// tolerance is reported as firing at its due time. At least zero and shorter than the retry period; the
    /// default is shorter than any retry period the runtime accepts.
    /// </summary>
    public TimeSpan DueTolerance { get; set; } = TimeSpan.FromMilliseconds(500);
}
