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
}
