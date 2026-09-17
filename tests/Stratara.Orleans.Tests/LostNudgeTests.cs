using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.Projections;
using Stratara.Diagnostics;
using Stratara.Contracts.Messages;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Projections;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A nudge that cannot be sent — the silo has not started or is stopping — is a lost nudge: the commit it follows
/// already happened, so the dispatch completes and the remaining targets are still nudged. It is logged with the
/// consumers it was meant for, so that a wake-up path that is always lost can be told from one lost once.
/// </summary>
public sealed class LostNudgeTests
{
    [Fact]
    public async Task A_nudge_that_throws_does_not_fail_the_dispatch_and_the_other_targets_are_nudged()
    {
        var failing = new Target(new NullReferenceException("the silo has not started"));
        var reached = new Target(failure: null);
        var dispatcher = new OrleansEventBundleDispatcher(
            new Mock<IGrainFactory>().Object,
            [failing, reached],
            new Mock<IProjectionReplayState>().Object,
            Options.Create(new CommitOrderOptions()));
        var streamId = Guid.NewGuid();
        var bundle = new EventBundle([new EventMessage(Guid.NewGuid(), 1, "{}", streamId, "Created", "Aggregate", Guid.Empty, Guid.Empty, Guid.Empty, null)], "{}");

        await dispatcher.EnqueueEventBundleAsync(bundle, TestContext.Current.CancellationToken);

        Assert.Equal(1, failing.Nudges);
        Assert.Equal(1, reached.Nudges);
    }

    [Fact]
    public async Task A_nudge_that_throws_is_logged_with_the_consumers_it_was_meant_for()
    {
        var logger = new RecordingLogger();
        var dispatcher = new OrleansEventBundleDispatcher(
            new Mock<IGrainFactory>().Object,
            [new Target(new NullReferenceException("the silo has not started"))],
            new Mock<IProjectionReplayState>().Object,
            Options.Create(new CommitOrderOptions()),
            new LoggerOf<OrleansEventBundleDispatcher>(logger));
        var bundle = new EventBundle([new EventMessage(Guid.NewGuid(), 1, "{}", Guid.NewGuid(), "Created", "Aggregate", Guid.Empty, Guid.Empty, Guid.Empty, null)], "{}");

        await dispatcher.EnqueueEventBundleAsync(bundle, TestContext.Current.CancellationToken);

        var logged = Assert.Single(logger.Entries, entry => entry.EventId.Id == LogEvents.Orleans.NudgeFailed);
        Assert.Equal(LogLevel.Debug, logged.Level);
        Assert.Contains("probe", logged.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_projection_whose_wake_up_cannot_be_sent_does_not_cost_the_others_theirs()
    {
        var woken = new List<string>();
        var handler = new Mock<IProjectionHandler>();
        var projections = new[] { Named(handler, "first"), Named(handler, "second"), Named(handler, "third") };
        var target = new ProjectionNudgeTarget(handler.Object, projections);
        var grains = new[] { "first", "second", "third" }.ToDictionary(
            name => StoreReaderGrainKey.Of(name, 0),
            name => name == "second" ? Refusing() : Woken(woken, name));
        var grainFactory = new Mock<IGrainFactory>();
        grainFactory.Setup(f => f.GetGrain<IProjectionGrain>(It.IsAny<string>(), null)).Returns((string key, string? _) => grains[key]);

        var nudge = target.NudgeAsync(grainFactory.Object, partition: 0);

        await Assert.ThrowsAsync<AggregateException>(() => nudge);
        Assert.Equal(2, woken.Count);
    }

    private static IProjection Named(Mock<IProjectionHandler> handler, string name)
    {
        var projection = new Mock<IProjection>();
        handler.Setup(h => h.GetProjectionName(projection.Object)).Returns(name);
        return projection.Object;
    }

    private static IProjectionGrain Woken(List<string> woken, string name) => new NudgedGrain(woken, name);

    /// <summary>A grain whose wake-up cannot even be sent — the call fails where it is made, not in a task.</summary>
    private static IProjectionGrain Refusing() => new NudgedGrain(woken: null, name: "refusing");

    /// <summary>
    /// A store reader that records its wake-up, or refuses it where it was given no list — the internal grain
    /// interface cannot be proxied, so the double is written by hand.
    /// </summary>
    private sealed class NudgedGrain(List<string>? woken, string name) : IProjectionGrain
    {
        public Task EnsureRunningAsync() => Task.CompletedTask;

        public Task NudgeAsync()
        {
            if (woken is null)
            {
                throw new InvalidOperationException("no silo of the cluster registered the projections role");
            }

            lock (woken)
            {
                woken.Add(name);
            }

            return Task.CompletedTask;
        }

        public Task<int> CatchUpAsync() => Task.FromResult(0);

        public Task<long> PositionAsync() => Task.FromResult(0L);

        public Task PauseAsync() => Task.CompletedTask;

        public Task ResumeAsync() => Task.CompletedTask;
    }

    /// <summary>A typed logger over the recording one, which the dispatcher takes.</summary>
    private sealed class LoggerOf<T>(RecordingLogger recording) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => recording.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => recording.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            recording.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class Target(Exception? failure) : INudgeTarget
    {
        public int Nudges { get; private set; }

        public IReadOnlyList<string> ConsumerNames => ["probe"];

        public Task NudgeAsync(IGrainFactory grainFactory, int partition)
        {
            Nudges++;
            return failure is null ? Task.CompletedTask : throw failure;
        }

        public Task EnsureRunningAsync(IGrainFactory grainFactory, int partition) => Task.CompletedTask;
    }
}
