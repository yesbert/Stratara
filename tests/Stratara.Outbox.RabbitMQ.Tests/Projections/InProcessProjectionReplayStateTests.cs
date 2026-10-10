using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Projections;
using Stratara.Diagnostics;
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
    public void TryActivate_TheSameRequestTwice_RunsItOnce()
    {
        var (state, _) = Create();
        var requestId = Guid.NewGuid();

        Assert.True(state.TryActivate(requestId));
        state.Complete(new ReplayCompletion(requestId, ReplayResult.Succeeded, 3));

        Assert.False(state.TryActivate(requestId));
        Assert.False(state.IsReplayActive);
    }

    [Fact]
    public void TryActivate_WhileActive_IsRefused()
    {
        var (state, _) = Create();
        var running = Guid.NewGuid();
        Assert.True(state.TryActivate(running));

        Assert.False(state.TryActivate(Guid.NewGuid()));

        Assert.Equal(running, state.GetProgress().RequestId);
    }

    [Fact]
    public void Complete_KeepsTheOutcomeUntilTheNextCompletes()
    {
        var (state, _) = Create();
        var first = Guid.NewGuid();
        state.TryActivate(first);
        state.SetProgress(7, 7);
        state.Complete(new ReplayCompletion(first, ReplayResult.Succeeded, 7));

        state.TryActivate(Guid.NewGuid());

        var whileTheNextRuns = state.GetProgress();
        Assert.True(whileTheNextRuns.IsActive);
        Assert.Equal(first, whileTheNextRuns.LastReplay!.RequestId);
        Assert.Equal(ReplayResult.Succeeded, whileTheNextRuns.LastReplay.Result);
        Assert.Equal(7, whileTheNextRuns.LastReplay.ReplayedEvents);
    }

    [Fact]
    public void Complete_StampsStartAndEndFromTheTimeProvider()
    {
        var (state, clock) = Create();
        var started = clock.Now;
        var requestId = Guid.NewGuid();
        state.TryActivate(requestId);
        clock.Now = started.AddSeconds(2);

        state.Complete(new ReplayCompletion(requestId, ReplayResult.Failed, 4, "boom"));

        var progress = state.GetProgress();
        Assert.False(progress.IsActive);
        Assert.Equal(0, progress.ProcessedEvents);
        Assert.Equal(0, progress.TotalEvents);
        Assert.Null(progress.RequestId);
        Assert.Equal("boom", progress.ErrorMessage);
        Assert.Equal(started, progress.LastReplay!.StartedAt);
        Assert.Equal(started.AddSeconds(2), progress.LastReplay.EndedAt);
        Assert.Equal(ReplayResult.Failed, progress.LastReplay.Result);
        Assert.Equal("boom", progress.LastReplay.ErrorMessage);
    }

    [Fact]
    public void Complete_OfAReplayThatOutlivedItsLease_LeavesTheNextReplayRunning()
    {
        var (state, clock) = Create(leaseSeconds: 10);
        var outlived = Guid.NewGuid();
        state.TryActivate(outlived);
        clock.Now = clock.Now.AddSeconds(11);
        var next = Guid.NewGuid();
        Assert.True(state.TryActivate(next));

        state.Complete(new ReplayCompletion(outlived, ReplayResult.Succeeded, 5));

        var progress = state.GetProgress();
        Assert.True(progress.IsActive);
        Assert.Equal(next, progress.RequestId);
        Assert.Equal(outlived, progress.LastReplay!.RequestId);
    }

    [Fact]
    public void AFailedCompletionOfAReplayThatOutlivedItsLease_LeavesTheRunningReplaysErrorAlone()
    {
        var (state, clock) = Create(leaseSeconds: 10);
        var outlived = Guid.NewGuid();
        state.TryActivate(outlived);
        clock.Now = clock.Now.AddSeconds(11);
        state.TryActivate(Guid.NewGuid());

        state.Complete(new ReplayCompletion(outlived, ReplayResult.Failed, 1, "late failure"));

        var progress = state.GetProgress();
        Assert.True(progress.IsActive);
        Assert.Null(progress.ErrorMessage);
        Assert.Equal("late failure", progress.LastReplay!.ErrorMessage);
    }

    [Fact]
    public void RequestReplay_WithAnEmptyIdentity_IsRefused()
    {
        var (state, _) = Create();

        Assert.Throws<ArgumentException>(() => state.RequestReplay(Guid.Empty));
    }

    [Fact]
    public void Initially_ReportsNoOutcome()
    {
        var (state, _) = Create();

        Assert.Null(state.GetProgress().LastReplay);
    }

    [Fact]
    public async Task RequestReplay_WithAnId_DeliversThatId()
    {
        var (state, _) = Create();
        var delivered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        await state.SubscribeToReplayRequestAsync(requestId =>
        {
            delivered.TrySetResult(requestId);
            return Task.CompletedTask;
        });
        var chosen = Guid.NewGuid();

        state.RequestReplay(chosen);

        Assert.Equal(chosen, await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
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

    [Fact]
    public async Task RequestReplay_ASubscriberThatThrowsSynchronously_DoesNotStopTheOthers()
    {
        var (state, _) = Create();
        var reached = false;
        await state.SubscribeToReplayRequestAsync(() => throw new InvalidOperationException("sync"));
        await state.SubscribeToReplayRequestAsync(() => { reached = true; return Task.CompletedTask; });

        state.RequestReplay();

        Assert.True(reached);
    }

    [Fact]
    public async Task RequestReplay_ASubscriberThatFaults_IsLoggedAndDoesNotStopTheOthers()
    {
        var logs = new List<EventId>();
        var clock = new ManualTimeProvider();
        var state = new InProcessProjectionReplayState(
            Options.Create(new ProjectionReplayOptions()),
            clock,
            new CapturingLogger<InProcessProjectionReplayState>(logs));
        var reached = new TaskCompletionSource();
        await state.SubscribeToReplayRequestAsync(async () => { await Task.Yield(); throw new InvalidOperationException("async"); });
        await state.SubscribeToReplayRequestAsync(() => { reached.SetResult(); return Task.CompletedTask; });

        state.RequestReplay();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.Contains(logs, id => id.Id == LogEvents.Projection.ProjectionReplayRequestSubscriberFailed);
    }

    private sealed class CapturingLogger<T>(List<EventId> sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (sink)
            {
                sink.Add(eventId);
            }
        }
    }

    private sealed record ReplayProgressShape(bool IsActive, long Processed, long Total, int Percentage, string? Error)
    {
        public static ReplayProgressShape Of(Stratara.Abstractions.Projections.ReplayProgress p) =>
            new(p.IsActive, p.ProcessedEvents, p.TotalEvents, p.Percentage, p.ErrorMessage);
    }
}
