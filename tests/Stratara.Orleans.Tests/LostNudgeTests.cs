using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.Projections;
using Stratara.Contracts.Messages;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Projections;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A nudge that cannot be sent — the silo has not started or is stopping — is a lost nudge: the commit it follows
/// already happened, so the dispatch completes and the remaining targets are still nudged.
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
