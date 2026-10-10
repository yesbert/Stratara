using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Stratara.Abstractions.Projections;
using Stratara.Outbox.RabbitMQ.Projections;
using Stratara.Outbox.RabbitMQ.IntegrationTests.Fixtures;

namespace Stratara.Outbox.RabbitMQ.IntegrationTests.Projections;

[Collection(RedisCollection.Name)]
public class ProjectionReplayStateTests(RedisFixture redis)
{
    private const string ActiveKey = "stratara:projection:replay:active";
    private const string ProcessedKey = "stratara:projection:replay:processed";
    private const string TotalKey = "stratara:projection:replay:total";
    private const string ErrorKey = "stratara:projection:replay:error";

    private static readonly TimeSpan MessageLatency = TimeSpan.FromSeconds(5);

    private ProjectionReplayState CreateSut(int leaseSeconds = 300, int refreshSeconds = 1) =>
        new(redis.Connection, Options.Create(new ProjectionReplayOptions { LeaseSeconds = leaseSeconds, RefreshSeconds = refreshSeconds }));

    private async Task<ProjectionReplayState> StartSutAsync(int leaseSeconds = 300, int refreshSeconds = 1)
    {
        var sut = CreateSut(leaseSeconds, refreshSeconds);
        await sut.FirstRefresh;
        return sut;
    }

    private TimeSpan? TimeToLive(string key) => redis.Connection.GetDatabase().KeyTimeToLive(key);

    private async Task<long> GetCallsAsync()
    {
        var server = redis.Connection.GetServer(redis.Connection.GetEndPoints()[0]);
        var stats = await server.InfoAsync("commandstats");
        var get = stats.SelectMany(group => group).FirstOrDefault(pair => pair.Key == "cmdstat_get");
        if (get.Key is null)
        {
            return 0;
        }

        var calls = get.Value.Split(',').First(part => part.StartsWith("calls=", StringComparison.Ordinal));
        return long.Parse(calls["calls=".Length..], System.Globalization.CultureInfo.InvariantCulture);
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

    [Fact]
    public async Task GetProgress_AfterASucceededReplay_ReportsItsOutcome()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync();
        var requestId = Guid.NewGuid();

        Assert.True(sut.TryActivate(requestId));
        sut.SetProgress(730, 730);
        sut.Complete(new ReplayCompletion(requestId, ReplayResult.Succeeded, 730));

        var progress = sut.GetProgress();
        Assert.False(progress.IsActive);
        Assert.Null(progress.RequestId);
        var outcome = Assert.IsType<ReplayOutcome>(progress.LastReplay);
        Assert.Equal(requestId, outcome.RequestId);
        Assert.Equal(ReplayResult.Succeeded, outcome.Result);
        Assert.Equal(730, outcome.ReplayedEvents);
        Assert.True(outcome.EndedAt >= outcome.StartedAt);
        Assert.Null(outcome.ErrorMessage);
    }

    [Fact]
    public async Task GetProgress_AfterAFailedReplay_ReportsZeroCountersAndAFailedOutcome()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync();

        var requestId = Guid.NewGuid();
        sut.TryActivate(requestId);
        sut.SetProgress(40, 100);
        sut.Complete(new ReplayCompletion(requestId, ReplayResult.Failed, 40, "projection X exploded"));

        var progress = sut.GetProgress();
        Assert.False(progress.IsActive);
        Assert.Equal(0, progress.ProcessedEvents);
        Assert.Equal(0, progress.TotalEvents);
        Assert.Equal("projection X exploded", progress.ErrorMessage);
        Assert.Equal(ReplayResult.Failed, progress.LastReplay!.Result);
        Assert.Equal(40, progress.LastReplay.ReplayedEvents);
        Assert.Equal("projection X exploded", progress.LastReplay.ErrorMessage);
    }

    [Fact]
    public async Task GetProgress_BeforeAnyReplay_ReportsNoOutcome()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync();

        Assert.Null(sut.GetProgress().LastReplay);
    }

    [Fact]
    public async Task ARequestSeenByTwoHosts_IsClaimedByOne()
    {
        await redis.FlushAsync();
        await using var first = await StartSutAsync();
        await using var second = await StartSutAsync();
        var requestId = Guid.NewGuid();

        var claims = new[] { first.TryActivate(requestId), second.TryActivate(requestId) };

        Assert.Single(claims, claimed => claimed);
    }

    [Fact]
    public async Task ARequestSeenAfterAShortReplayEnded_IsNotRunAgain()
    {
        await redis.FlushAsync();
        await using var first = await StartSutAsync();
        await using var late = await StartSutAsync();
        var requestId = Guid.NewGuid();

        Assert.True(first.TryActivate(requestId));
        first.Complete(new ReplayCompletion(requestId, ReplayResult.Succeeded, 2));

        Assert.False(late.TryActivate(requestId));
        Assert.False(late.GetProgress().IsActive);
    }

    [Fact]
    public async Task TryActivate_WhileAnotherReplayIsActive_IsRefusedAndLeavesItRunning()
    {
        await redis.FlushAsync();
        await using var first = await StartSutAsync();
        await using var second = await StartSutAsync();
        var running = Guid.NewGuid();
        first.TryActivate(running);

        Assert.False(second.TryActivate(Guid.NewGuid()));

        var progress = second.GetProgress();
        Assert.True(progress.IsActive);
        Assert.Equal(running, progress.RequestId);
    }

    [Fact]
    public async Task Complete_OfAReplayThatOutlivedItsLease_LeavesTheNextReplayRunning()
    {
        await redis.FlushAsync();
        await using var outlivedHost = await StartSutAsync();
        await using var nextHost = await StartSutAsync();
        var outlived = Guid.NewGuid();
        outlivedHost.TryActivate(outlived);
        await redis.Connection.GetDatabase().KeyDeleteAsync(ActiveKey);
        var next = Guid.NewGuid();
        Assert.True(nextHost.TryActivate(next));

        outlivedHost.Complete(new ReplayCompletion(outlived, ReplayResult.Succeeded, 5));

        var progress = nextHost.GetProgress();
        Assert.True(progress.IsActive);
        Assert.Equal(next, progress.RequestId);
        Assert.Equal(outlived, progress.LastReplay!.RequestId);
    }

    [Fact]
    public async Task AFailedCompletionOfAReplayThatOutlivedItsLease_LeavesTheRunningReplaysErrorAndMarkingAlone()
    {
        await redis.FlushAsync();
        await using var outlivedHost = await StartSutAsync();
        await using var nextHost = await StartSutAsync();
        var outlived = Guid.NewGuid();
        outlivedHost.TryActivate(outlived);
        await redis.Connection.GetDatabase().KeyDeleteAsync(ActiveKey);
        nextHost.TryActivate(Guid.NewGuid());

        outlivedHost.Complete(new ReplayCompletion(outlived, ReplayResult.Failed, 1, "late failure"));

        var progress = nextHost.GetProgress();
        Assert.True(progress.IsActive);
        Assert.Null(progress.ErrorMessage);
        Assert.Equal("late failure", progress.LastReplay!.ErrorMessage);
        Assert.True(outlivedHost.IsReplayActive);
    }

    [Fact]
    public async Task RequestReplay_WithAnEmptyIdentity_IsRefused()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync();

        Assert.Throws<ArgumentException>(() => sut.RequestReplay(Guid.Empty));
    }

    [Fact]
    public async Task SetProgress_RenewsTheLeaseOfTheRunningRequestsIdentity()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync(leaseSeconds: 60);
        sut.TryActivate(Guid.NewGuid());
        await redis.Connection.GetDatabase().KeyExpireAsync("stratara:projection:replay:request-id", TimeSpan.FromSeconds(5));

        sut.SetProgress(1, 2);

        Assert.True(TimeToLive("stratara:projection:replay:request-id") > TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task RequestReplay_WithAnId_DeliversThatId()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync();
        var delivered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        await sut.SubscribeToReplayRequestAsync(requestId =>
        {
            delivered.TrySetResult(requestId);
            return Task.CompletedTask;
        });
        var chosen = Guid.NewGuid();

        sut.RequestReplay(chosen);

        Assert.Equal(chosen, await delivered.Task.WaitAsync(MessageLatency));
    }

    [Fact]
    public async Task ALegacyRequestPayload_IsRunUnderAFreshId()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync();
        var delivered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        await sut.SubscribeToReplayRequestAsync(requestId =>
        {
            delivered.TrySetResult(requestId);
            return Task.CompletedTask;
        });

        await redis.Connection.GetSubscriber().PublishAsync(RedisChannel.Literal("stratara:projection:replay:request"), "replay");

        Assert.NotEqual(Guid.Empty, await delivered.Task.WaitAsync(MessageLatency));
    }

    [Fact]
    public async Task IsReplayActive_MakesNoRequestToTheCoordinationStore()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync(refreshSeconds: 200);
        var before = await GetCallsAsync();

        for (var i = 0; i < 1000; i++)
        {
            _ = sut.IsReplayActive;
        }

        Assert.Equal(before, await GetCallsAsync());
    }

    [Fact]
    public async Task Activate_IsSeenOnTheSameHostAtOnce()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync(refreshSeconds: 200);

        sut.Activate();
        Assert.True(sut.IsReplayActive);

        sut.Deactivate();
        Assert.False(sut.IsReplayActive);
    }

    [Fact]
    public async Task Activate_OnOneHost_IsSeenOnAnotherWithinTheRefreshPeriod()
    {
        await redis.FlushAsync();
        await using var replaying = await StartSutAsync(refreshSeconds: 200);
        await using var other = await StartSutAsync(refreshSeconds: 200);

        replaying.Activate();

        Assert.True(await WaitUntilAsync(() => other.IsReplayActive, MessageLatency), "the other host did not learn of the replay from the announcement");
    }

    [Fact]
    public async Task Deactivate_OnOneHost_IsSeenOnAnotherWithinTheRefreshPeriod()
    {
        await redis.FlushAsync();
        await using var replaying = await StartSutAsync(refreshSeconds: 200);
        await using var other = await StartSutAsync(refreshSeconds: 200);
        replaying.Activate();
        Assert.True(await WaitUntilAsync(() => other.IsReplayActive, MessageLatency));

        replaying.Deactivate();

        Assert.True(await WaitUntilAsync(() => !other.IsReplayActive, MessageLatency), "the other host did not learn that the replay ended");
    }

    [Fact]
    public async Task SetFailed_OnOneHost_IsSeenOnAnotherWithinTheRefreshPeriod()
    {
        await redis.FlushAsync();
        await using var replaying = await StartSutAsync(refreshSeconds: 200);
        await using var other = await StartSutAsync(refreshSeconds: 200);
        replaying.Activate();
        Assert.True(await WaitUntilAsync(() => other.IsReplayActive, MessageLatency));

        replaying.SetFailed("projection X exploded");

        Assert.True(await WaitUntilAsync(() => !other.IsReplayActive, MessageLatency), "the other host did not learn that the replay failed");
    }

    [Fact]
    public async Task AMarkingDeletedBehindTheHostsBack_IsSeenInactiveWithinTheRefreshPeriod()
    {
        await redis.FlushAsync();
        await using var replaying = await StartSutAsync(refreshSeconds: 1);
        await using var other = await StartSutAsync(refreshSeconds: 1);
        replaying.Activate();
        Assert.True(await WaitUntilAsync(() => other.IsReplayActive, MessageLatency));

        await redis.Connection.GetDatabase().KeyDeleteAsync(ActiveKey);

        var withinThreePeriods = TimeSpan.FromSeconds(3);
        Assert.True(await WaitUntilAsync(() => !replaying.IsReplayActive, withinThreePeriods), "the replaying host kept answering active after the marking was gone");
        Assert.True(await WaitUntilAsync(() => !other.IsReplayActive, withinThreePeriods), "the other host kept answering active after the marking was gone");
    }

    [Fact]
    public async Task IsReplayActive_ReturnsFalseOnEmptyState()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync();

        Assert.False(sut.IsReplayActive);
    }

    [Fact]
    public async Task Activate_SetsIsReplayActiveTrue()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync();

        sut.Activate();

        Assert.True(sut.IsReplayActive);
    }

    [Fact]
    public async Task Deactivate_ClearsActiveFlagAndProgressCounters()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync();

        sut.Activate();
        sut.SetProgress(processedEvents: 50, totalEvents: 100);
        sut.Deactivate();

        var progress = sut.GetProgress();
        Assert.False(progress.IsActive);
        Assert.Equal(0, progress.ProcessedEvents);
        Assert.Equal(0, progress.TotalEvents);
        Assert.Equal(0, progress.Percentage);
        Assert.Null(progress.ErrorMessage);
    }

    [Fact]
    public async Task SetProgress_UpdatesProcessedAndTotal_AndComputesPercentage()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync();

        sut.Activate();
        sut.SetProgress(processedEvents: 25, totalEvents: 100);

        var progress = sut.GetProgress();
        Assert.True(progress.IsActive);
        Assert.Equal(25, progress.ProcessedEvents);
        Assert.Equal(100, progress.TotalEvents);
        Assert.Equal(25, progress.Percentage);
    }

    [Fact]
    public async Task GetProgress_ReturnsZeroPercentage_WhenTotalIsZero()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync();

        sut.Activate();
        sut.SetProgress(processedEvents: 0, totalEvents: 0);

        var progress = sut.GetProgress();
        Assert.Equal(0, progress.Percentage);
    }

    [Fact]
    public async Task SetFailed_RecordsErrorMessageAndClearsActiveFlag()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync();

        sut.Activate();
        sut.SetFailed("projection X exploded");

        var progress = sut.GetProgress();
        Assert.False(progress.IsActive);
        Assert.Equal("projection X exploded", progress.ErrorMessage);
    }

    [Fact]
    public async Task Activate_ClearsPreviousErrorMessage()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync();

        sut.SetFailed("earlier failure");
        sut.Activate();

        var progress = sut.GetProgress();
        Assert.True(progress.IsActive);
        Assert.Null(progress.ErrorMessage);
    }

    [Fact]
    public async Task RequestReplay_FiresSubscriberCallback()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync();

        var tcs = new TaskCompletionSource();
        await sut.SubscribeToReplayRequestAsync(() =>
        {
            tcs.TrySetResult();
            return Task.CompletedTask;
        });

        sut.RequestReplay();

        // Pub/sub delivery has a small delay; wait with a generous timeout.
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(tcs.Task, completed);
    }

    [Fact]
    public async Task Activate_LeasesTheActiveMarking()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync(leaseSeconds: 300);

        sut.Activate();

        var remaining = TimeToLive(ActiveKey);
        Assert.NotNull(remaining);
        Assert.InRange(remaining.Value, TimeSpan.FromSeconds(290), TimeSpan.FromSeconds(300));
    }

    [Fact]
    public async Task SetProgress_LeasesTheProgressCounters()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync(leaseSeconds: 300);

        sut.Activate();
        sut.SetProgress(processedEvents: 25, totalEvents: 100);

        foreach (var key in new[] { ProcessedKey, TotalKey })
        {
            var remaining = TimeToLive(key);
            Assert.NotNull(remaining);
            Assert.InRange(remaining.Value, TimeSpan.FromSeconds(290), TimeSpan.FromSeconds(300));
        }
    }

    [Fact]
    public async Task SetProgress_RenewsTheActiveMarkingsLease()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync(leaseSeconds: 10);

        sut.Activate();
        await Task.Delay(TimeSpan.FromSeconds(3));
        var beforeRenewal = TimeToLive(ActiveKey);
        Assert.NotNull(beforeRenewal);
        Assert.True(beforeRenewal.Value < TimeSpan.FromSeconds(8),
            $"expected the lease to have decayed below 8s before renewal, was {beforeRenewal}");

        sut.SetProgress(processedEvents: 1, totalEvents: 100);

        var afterRenewal = TimeToLive(ActiveKey);
        Assert.NotNull(afterRenewal);
        Assert.True(afterRenewal.Value > beforeRenewal.Value,
            $"expected the lease to be renewed, was {beforeRenewal} before and {afterRenewal} after");
    }

    [Fact]
    public async Task ActiveMarking_LapsesWhenNobodyRenewsIt()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync(leaseSeconds: 2);

        sut.Activate();
        sut.SetProgress(processedEvents: 188_000, totalEvents: 280_261);
        Assert.True(sut.IsReplayActive);

        await Task.Delay(TimeSpan.FromSeconds(4));

        Assert.False(sut.IsReplayActive);
        var progress = sut.GetProgress();
        Assert.False(progress.IsActive);
        Assert.Equal(0, progress.ProcessedEvents);
        Assert.Equal(0, progress.TotalEvents);
    }

    [Fact]
    public async Task SetFailed_KeepsTheRecordedErrorReadableWithoutALease()
    {
        await redis.FlushAsync();
        await using var sut = await StartSutAsync(leaseSeconds: 300);

        sut.Activate();
        sut.SetFailed("projection replay blew up");

        Assert.Null(TimeToLive(ErrorKey));
        Assert.Equal("projection replay blew up", sut.GetProgress().ErrorMessage);
    }
}
