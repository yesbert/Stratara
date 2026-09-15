namespace Stratara.Orleans.Timers;

/// <summary>
/// Whether a tick is due. The due time was computed on the host that registered the timer and the tick runs on
/// a silo with a clock of its own, so a tick earlier than the due time by less than the tolerance is due; it is
/// reported as firing at the due time, never before it.
/// </summary>
internal static class TimerDueTime
{
    /// <summary>Returns whether a tick now is due, and the moment it is reported to have fired.</summary>
    /// <param name="clock">The silo's clock.</param>
    /// <param name="dueAt">When the timer is due.</param>
    /// <param name="tolerance">How much earlier than <paramref name="dueAt"/> a tick still counts as due.</param>
    /// <param name="firedAt">The later of now and <paramref name="dueAt"/>.</param>
    public static bool IsDue(TimeProvider clock, DateTimeOffset dueAt, TimeSpan tolerance, out DateTimeOffset firedAt)
    {
        var now = clock.GetUtcNow();
        firedAt = now < dueAt ? dueAt : now;
        return now + tolerance >= dueAt;
    }
}
