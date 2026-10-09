using Stratara.Abstractions.Projections;

namespace Stratara.Projections.Tests.Services;

/// <summary>
/// An implementation of <see cref="IProjectionReplayState"/> written before the request identity, the claim and the
/// outcome existed keeps working through the interface's defaults: it activates, refuses while active, and ends the
/// replay through the members it already has.
/// </summary>
public class ProjectionReplayStateDefaultsTests
{
    private sealed class EarlierReplayState : IProjectionReplayState
    {
        public bool IsReplayActive { get; private set; }
        public List<string> Calls { get; } = [];
        public Func<Task>? Subscriber { get; private set; }

        public void Activate()
        {
            IsReplayActive = true;
            Calls.Add(nameof(Activate));
        }

        public void Deactivate()
        {
            IsReplayActive = false;
            Calls.Add(nameof(Deactivate));
        }

        public void SetFailed(string errorMessage)
        {
            IsReplayActive = false;
            Calls.Add($"{nameof(SetFailed)}:{errorMessage}");
        }

        public Task SubscribeToReplayRequestAsync(Func<Task> onReplayRequested, CancellationToken cancellationToken = default)
        {
            Subscriber = onReplayRequested;
            return Task.CompletedTask;
        }

        public void RequestReplay() => Subscriber?.Invoke();

        public void SetProgress(long processedEvents, long totalEvents)
        {
        }

        public ReplayProgress GetProgress() => new(IsReplayActive, 0, 0, 0);
    }

    [Fact]
    public void TryActivate_ActivatesAndThenRefusesWhileActive()
    {
        var state = new EarlierReplayState();
        IProjectionReplayState replay = state;

        Assert.True(replay.TryActivate(Guid.NewGuid()));
        Assert.False(replay.TryActivate(Guid.NewGuid()));
        Assert.Equal([nameof(EarlierReplayState.Activate)], state.Calls);
    }

    [Fact]
    public void Complete_EndsThroughDeactivateOrSetFailed()
    {
        var state = new EarlierReplayState();
        IProjectionReplayState replay = state;

        replay.Complete(new ReplayCompletion(Guid.NewGuid(), ReplayResult.Succeeded, 1));
        replay.Complete(new ReplayCompletion(Guid.NewGuid(), ReplayResult.Interrupted, 1));
        replay.Complete(new ReplayCompletion(Guid.NewGuid(), ReplayResult.Failed, 1, "boom"));

        Assert.Equal(["Deactivate", "Deactivate", "SetFailed:boom"], state.Calls);
    }

    [Fact]
    public async Task SubscribeWithAnIdentity_IsCalledWithAnEmptyIdentity()
    {
        var state = new EarlierReplayState();
        IProjectionReplayState replay = state;
        Guid? received = null;
        await replay.SubscribeToReplayRequestAsync(requestId =>
        {
            received = requestId;
            return Task.CompletedTask;
        });

        replay.RequestReplay(Guid.NewGuid());

        Assert.Equal(Guid.Empty, received);
    }
}
