using Stratara.Outbox.RabbitMQ.Projections;
using Stratara.Outbox.RabbitMQ.IntegrationTests.Fixtures;

namespace Stratara.Outbox.RabbitMQ.IntegrationTests.Projections;

[Collection(RedisCollection.Name)]
public class ProjectionReplayStateTests(RedisFixture redis)
{
    private ProjectionReplayState CreateSut() => new(redis.Connection);

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
}
