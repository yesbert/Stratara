using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using Stratara.Diagnostics;
using Stratara.Outbox.RabbitMQ.Projections;

namespace Stratara.Outbox.RabbitMQ.Tests.Projections;

/// <summary>
/// The Redis-backed state against a connection the test controls: what a refresh does while a
/// transition of the host's own overtakes it, and what a subscription that cannot be established
/// does to the refresh's logging. The real connection is exercised by the integration tests.
/// </summary>
public class ProjectionReplayStateTests
{
    private sealed class ControlledRedis
    {
        public Mock<IDatabase> Database { get; } = new();
        public Mock<ISubscriber> Subscriber { get; } = new();
        public Mock<IConnectionMultiplexer> Connection { get; } = new();
        public Queue<Task<RedisValue>> Reads { get; } = new();
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeLogger<ProjectionReplayState> Logger { get; } = new();

        public ControlledRedis()
        {
            Connection.Setup(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(Database.Object);
            Connection.Setup(c => c.GetSubscriber(It.IsAny<object>())).Returns(Subscriber.Object);
            Database.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns(() =>
                {
                    ReadStarted.TrySetResult();
                    return Reads.Count > 0 ? Reads.Dequeue() : Task.FromResult(RedisValue.Null);
                });
        }

        public ProjectionReplayState CreateState(int refreshSeconds = 200) =>
            new(Connection.Object, Options.Create(new ProjectionReplayOptions { LeaseSeconds = 300, RefreshSeconds = refreshSeconds }), TimeProvider.System, Logger);
    }

    [Fact]
    public async Task A_refresh_in_flight_during_the_hosts_own_activation_does_not_overwrite_it()
    {
        var redis = new ControlledRedis();
        var staleRead = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        redis.Reads.Enqueue(staleRead.Task);
        await using var state = redis.CreateState();
        await redis.ReadStarted.Task;
        Assert.False(state.IsReplayActive);

        state.Activate();
        Assert.True(state.IsReplayActive);

        staleRead.SetResult(RedisValue.Null);
        await state.FirstRefresh;

        Assert.True(state.IsReplayActive);
    }

    [Fact]
    public async Task A_refresh_in_flight_during_the_hosts_own_deactivation_does_not_overwrite_it()
    {
        var redis = new ControlledRedis();
        var staleRead = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        redis.Reads.Enqueue(staleRead.Task);
        await using var state = redis.CreateState();
        await redis.ReadStarted.Task;
        state.Activate();

        state.Deactivate();
        staleRead.SetResult("true");
        await state.FirstRefresh;

        Assert.False(state.IsReplayActive);
    }

    [Fact]
    public async Task A_refresh_that_completes_without_a_transition_sets_the_field()
    {
        var redis = new ControlledRedis();
        redis.Reads.Enqueue(Task.FromResult<RedisValue>("true"));
        await using var state = redis.CreateState();

        await state.FirstRefresh;

        Assert.True(state.IsReplayActive);
    }

    [Fact]
    public async Task A_subscription_that_cannot_be_established_is_logged_once_and_does_not_touch_the_refresh_log()
    {
        var redis = new ControlledRedis();
        redis.Subscriber.Setup(s => s.SubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new InvalidOperationException("no subscriber connection"));
        await using var state = redis.CreateState(refreshSeconds: 1);

        await state.FirstRefresh;
        await Task.Delay(TimeSpan.FromMilliseconds(2500));

        var events = redis.Logger.Collector.GetSnapshot().Select(e => e.Id.Id).ToList();
        Assert.Single(events, id => id == LogEvents.Projection.ProjectionReplayStateSubscriptionFailed);
        Assert.DoesNotContain(LogEvents.Projection.ProjectionReplayRefreshFailed, events);
        Assert.DoesNotContain(LogEvents.Projection.ProjectionReplayRefreshRecovered, events);
        Assert.DoesNotContain(LogEvents.Projection.ProjectionReplayStateSubscribed, events);
    }

    [Fact]
    public async Task A_refresh_that_fails_is_logged_once_per_stretch_and_its_recovery_once()
    {
        var redis = new ControlledRedis();
        var failure = new InvalidOperationException("gone");
        redis.Reads.Enqueue(Task.FromException<RedisValue>(failure));
        redis.Reads.Enqueue(Task.FromException<RedisValue>(failure));
        redis.Reads.Enqueue(Task.FromResult<RedisValue>("true"));
        await using var state = redis.CreateState(refreshSeconds: 1);

        Assert.True(await WaitUntilAsync(() => state.IsReplayActive, TimeSpan.FromSeconds(10)), "the third refresh never landed");

        var events = redis.Logger.Collector.GetSnapshot().Select(e => e.Id.Id).ToList();
        Assert.Single(events, id => id == LogEvents.Projection.ProjectionReplayRefreshFailed);
        Assert.Single(events, id => id == LogEvents.Projection.ProjectionReplayRefreshRecovered);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        return true;
    }
}
