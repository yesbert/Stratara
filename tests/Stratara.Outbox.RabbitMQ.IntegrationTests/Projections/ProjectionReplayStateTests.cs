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

    private ProjectionReplayState CreateSut(int leaseSeconds = 300) =>
        new(redis.Connection, Options.Create(new ProjectionReplayOptions { LeaseSeconds = leaseSeconds }));

    private TimeSpan? TimeToLive(string key) => redis.Connection.GetDatabase().KeyTimeToLive(key);

    [Fact]
    public async Task IsReplayActive_ReturnsFalseOnEmptyState()
    {
        await redis.FlushAsync();
        var sut = CreateSut();

        Assert.False(sut.IsReplayActive);
    }

    [Fact]
    public async Task Activate_SetsIsReplayActiveTrue()
    {
        await redis.FlushAsync();
        var sut = CreateSut();

        sut.Activate();

        Assert.True(sut.IsReplayActive);
    }

    [Fact]
    public async Task Deactivate_ClearsActiveFlagAndProgressCounters()
    {
        await redis.FlushAsync();
        var sut = CreateSut();

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
        var sut = CreateSut();

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
        var sut = CreateSut();

        sut.Activate();
        sut.SetProgress(processedEvents: 0, totalEvents: 0);

        var progress = sut.GetProgress();
        Assert.Equal(0, progress.Percentage);
    }

    [Fact]
    public async Task SetFailed_RecordsErrorMessageAndClearsActiveFlag()
    {
        await redis.FlushAsync();
        var sut = CreateSut();

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
        var sut = CreateSut();

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
        var sut = CreateSut();

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
        var sut = CreateSut(leaseSeconds: 300);

        sut.Activate();

        var remaining = TimeToLive(ActiveKey);
        Assert.NotNull(remaining);
        Assert.InRange(remaining.Value, TimeSpan.FromSeconds(290), TimeSpan.FromSeconds(300));
    }

    [Fact]
    public async Task SetProgress_LeasesTheProgressCounters()
    {
        await redis.FlushAsync();
        var sut = CreateSut(leaseSeconds: 300);

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
        var sut = CreateSut(leaseSeconds: 10);

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
        var sut = CreateSut(leaseSeconds: 2);

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
        var sut = CreateSut(leaseSeconds: 300);

        sut.Activate();
        sut.SetFailed("projection replay blew up");

        Assert.Null(TimeToLive(ErrorKey));
        Assert.Equal("projection replay blew up", sut.GetProgress().ErrorMessage);
    }
}
