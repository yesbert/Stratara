using Microsoft.Extensions.Options;
using Stratara.Outbox.RabbitMQ.Projections;

namespace Stratara.Outbox.RabbitMQ.Tests.Projections;

public class InProcessProjectionReplayStateTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (InProcessProjectionReplayState State, ManualTimeProvider Clock) Create(int leaseSeconds = 300)
    {
        var clock = new ManualTimeProvider();
        var state = new InProcessProjectionReplayState(
            Options.Create(new ProjectionReplayOptions { LeaseSeconds = leaseSeconds }),
            clock);
        return (state, clock);
    }

    [Fact]
    public void Initially_InactiveWithNoProgress()
    {
        var (state, _) = Create();

        Assert.False(state.IsReplayActive);
        Assert.Equal(new ReplayProgressShape(false, 0, 0, 0, null), ReplayProgressShape.Of(state.GetProgress()));
    }

    [Fact]
    public void Activate_MarksActiveAndClearsAnEarlierError()
    {
        var (state, _) = Create();
        state.SetFailed("earlier");

        state.Activate();

        Assert.True(state.IsReplayActive);
        Assert.Null(state.GetProgress().ErrorMessage);
    }

    [Fact]
    public void SetProgress_ReportsCountsAndPercentage()
    {
        var (state, _) = Create();
        state.Activate();

        state.SetProgress(25, 100);

        var progress = state.GetProgress();
        Assert.Equal(25, progress.ProcessedEvents);
        Assert.Equal(100, progress.TotalEvents);
        Assert.Equal(25, progress.Percentage);
    }

    [Fact]
    public void SetProgress_WithTotalZero_YieldsZeroPercent()
    {
        var (state, _) = Create();
        state.Activate();

        state.SetProgress(0, 0);

        Assert.Equal(0, state.GetProgress().Percentage);
    }

    [Fact]
    public void SetFailed_ClearsTheMarkingAndKeepsTheMessage()
    {
        var (state, _) = Create();
        state.Activate();

        state.SetFailed("boom");

        Assert.False(state.IsReplayActive);
        Assert.Equal("boom", state.GetProgress().ErrorMessage);
    }

    [Fact]
    public void Deactivate_ClearsEverything()
    {
        var (state, _) = Create();
        state.Activate();
        state.SetProgress(5, 10);

        state.Deactivate();

        Assert.Equal(new ReplayProgressShape(false, 0, 0, 0, null), ReplayProgressShape.Of(state.GetProgress()));
    }

    [Fact]
    public void Lease_ExpiresWithoutRenewal_AndIsRenewedByProgress()
    {
        var (state, clock) = Create(leaseSeconds: 60);
        state.Activate();

        clock.Now += TimeSpan.FromSeconds(59);
        Assert.True(state.IsReplayActive);

        state.SetProgress(1, 2);
        clock.Now += TimeSpan.FromSeconds(59);
        Assert.True(state.IsReplayActive);

        clock.Now += TimeSpan.FromSeconds(2);
        Assert.False(state.IsReplayActive);
        Assert.Equal(0, state.GetProgress().ProcessedEvents);
    }

    [Fact]
    public async Task RequestReplay_InvokesEverySubscriberOnce()
    {
        var (state, _) = Create();
        var calls = 0;
        await state.SubscribeToReplayRequestAsync(() => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
        await state.SubscribeToReplayRequestAsync(() => { Interlocked.Increment(ref calls); return Task.CompletedTask; });

        state.RequestReplay();

        Assert.Equal(2, calls);
    }

    private sealed record ReplayProgressShape(bool IsActive, long Processed, long Total, int Percentage, string? Error)
    {
        public static ReplayProgressShape Of(Stratara.Abstractions.Projections.ReplayProgress p) =>
            new(p.IsActive, p.ProcessedEvents, p.TotalEvents, p.Percentage, p.ErrorMessage);
    }
}
