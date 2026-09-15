using Microsoft.Extensions.Time.Testing;
using Stratara.Orleans.Timers;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A tick that arrives a moment before its due time, on a silo whose clock differs from the registering
/// host's, fires instead of waiting a whole retry period; it is reported as firing at the due time.
/// </summary>
public sealed class TimerDueTimeTests
{
    private static readonly DateTimeOffset DueAt = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    [Fact]
    public void A_tick_slightly_before_the_due_time_fires_at_the_due_time()
    {
        var clock = new FakeTimeProvider(DueAt - TimeSpan.FromMilliseconds(300));

        Assert.True(TimerDueTime.IsDue(clock, DueAt, Tolerance, out var firedAt));
        Assert.Equal(DueAt, firedAt);
    }

    [Fact]
    public void A_tick_earlier_than_the_tolerance_waits()
    {
        var clock = new FakeTimeProvider(DueAt - TimeSpan.FromSeconds(5));

        Assert.False(TimerDueTime.IsDue(clock, DueAt, Tolerance, out _));
    }

    [Fact]
    public void A_late_tick_fires_at_the_moment_it_arrives()
    {
        var clock = new FakeTimeProvider(DueAt + TimeSpan.FromSeconds(7));

        Assert.True(TimerDueTime.IsDue(clock, DueAt, Tolerance, out var firedAt));
        Assert.Equal(DueAt + TimeSpan.FromSeconds(7), firedAt);
    }
}
