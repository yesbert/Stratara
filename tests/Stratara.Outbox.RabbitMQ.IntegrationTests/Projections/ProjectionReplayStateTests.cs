using Microsoft.Extensions.Options;
using StackExchange.Redis;
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
