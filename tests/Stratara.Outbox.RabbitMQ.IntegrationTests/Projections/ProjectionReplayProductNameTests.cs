using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Stratara.Abstractions.Projections;
using Stratara.Outbox.RabbitMQ.IntegrationTests.Fixtures;
using Stratara.Outbox.RabbitMQ.Projections;

namespace Stratara.Outbox.RabbitMQ.IntegrationTests.Projections;

/// <summary>Two products on one Redis: what one product's replay reaches, and what it must not.</summary>
[Collection(RedisCollection.Name)]
public class ProjectionReplayProductNameTests(RedisFixture redis)
{
    private static readonly string[] ExpectedChannels = ["request", "state"];

    private const string ProductA = "product-a";
    private const string ProductB = "product-b";

    private static readonly TimeSpan MessageLatency = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SilenceWindow = TimeSpan.FromSeconds(1);

    private async Task<ProjectionReplayState> StartHostAsync(string? productName)
    {
        var host = new ProjectionReplayState(
            redis.Connection,
            Options.Create(new ProjectionReplayOptions { LeaseSeconds = 300, RefreshSeconds = 1 }),
            coordination: Options.Create(new RedisCoordinationOptions { ProductName = productName }));
        await host.FirstRefresh;
        return host;
    }

    private static async Task<TaskCompletionSource<Guid>> ListenAsync(ProjectionReplayState host)
    {
        var received = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        await host.SubscribeToReplayRequestAsync(requestId =>
        {
            received.TrySetResult(requestId);
            return Task.CompletedTask;
        });
        return received;
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
    public async Task ARequest_ReachesEveryHostOfItsProduct_AndNoHostOfTheOther()
    {
        await redis.FlushAsync();
        await using var requester = await StartHostAsync(ProductA);
        await using var worker = await StartHostAsync(ProductA);
        await using var other = await StartHostAsync(ProductB);
        var atWorker = await ListenAsync(worker);
        var atOther = await ListenAsync(other);
        var requestId = Guid.CreateVersion7();

        requester.RequestReplay(requestId);

        Assert.Equal(requestId, await atWorker.Task.WaitAsync(MessageLatency));
        await Task.Delay(SilenceWindow);
        Assert.False(atOther.Task.IsCompleted);
    }

    [Fact]
    public async Task ARunningReplay_IsSeenByEveryHostOfItsProduct_AndNoHostOfTheOther()
    {
        await redis.FlushAsync();
        await using var runner = await StartHostAsync(ProductA);
        await using var peer = await StartHostAsync(ProductA);
        await using var other = await StartHostAsync(ProductB);
        var requestId = Guid.CreateVersion7();

        Assert.True(runner.TryActivate(requestId));
        runner.SetProgress(3, 10);

        Assert.True(await WaitUntilAsync(() => peer.IsReplayActive, MessageLatency));
        var atPeer = peer.GetProgress();
        Assert.True(atPeer.IsActive);
        Assert.Equal(requestId, atPeer.RequestId);
        Assert.Equal(3, atPeer.ProcessedEvents);

        await Task.Delay(SilenceWindow);
        Assert.False(other.IsReplayActive);
        var atOther = other.GetProgress();
        Assert.False(atOther.IsActive);
        Assert.Null(atOther.RequestId);
        Assert.Equal(0, atOther.ProcessedEvents);
    }

    [Fact]
    public async Task EachProduct_CanRunAReplayWhileTheOtherRunsOne_EvenUnderTheSameRequest()
    {
        await redis.FlushAsync();
        await using var inA = await StartHostAsync(ProductA);
        await using var inB = await StartHostAsync(ProductB);
        var requestId = Guid.CreateVersion7();

        Assert.True(inA.TryActivate(requestId));
        Assert.True(inB.TryActivate(requestId));
    }

    [Fact]
    public async Task AnOutcome_IsSeenByEveryHostOfItsProduct_AndNoHostOfTheOther()
    {
        await redis.FlushAsync();
        await using var runner = await StartHostAsync(ProductA);
        await using var peer = await StartHostAsync(ProductA);
        await using var other = await StartHostAsync(ProductB);
        var requestId = Guid.CreateVersion7();

        Assert.True(runner.TryActivate(requestId));
        runner.Complete(new ReplayCompletion(requestId, ReplayResult.Failed, 7, "no truncator"));

        var atPeer = peer.GetProgress();
        Assert.Equal(requestId, atPeer.LastReplay?.RequestId);
        Assert.Equal("no truncator", atPeer.ErrorMessage);

        var atOther = other.GetProgress();
        Assert.Null(atOther.LastReplay);
        Assert.Null(atOther.ErrorMessage);
    }

    [Fact]
    public async Task EveryKeyAndChannelAProductWrites_CarriesItsName()
    {
        await redis.FlushAsync();
        await using var host = await StartHostAsync(ProductA);
        var channels = new ConcurrentQueue<string>();
        var subscriber = redis.Connection.GetSubscriber();
        var everything = RedisChannel.Pattern("*");
        await subscriber.SubscribeAsync(everything, (channel, _) => channels.Enqueue(channel.ToString()));
        var requestId = Guid.CreateVersion7();
        var keys = new HashSet<string>();

        try
        {
            host.RequestReplay(requestId);
            Assert.True(host.TryActivate(requestId));
            host.SetProgress(1, 2);
            keys.UnionWith(KeysInTheStore());
            host.Complete(new ReplayCompletion(requestId, ReplayResult.Failed, 1, "failed"));
            keys.UnionWith(KeysInTheStore());
            Assert.True(await WaitUntilAsync(() => channels.Distinct().Count() >= 2, MessageLatency));
        }
        finally
        {
            await subscriber.UnsubscribeAsync(everything);
        }

        var prefix = $"stratara:{ProductA}:projection:replay:";
        Assert.Equal(
            new[] { "active", "claimed:" + requestId.ToString("N"), "error", "last", "processed", "request-id", "total" },
            keys.Select(key => key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key).Order(StringComparer.Ordinal));
        Assert.Equal(
            ExpectedChannels,
            channels.Distinct().Select(channel => channel.StartsWith(prefix, StringComparison.Ordinal) ? channel[prefix.Length..] : channel).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task HostsThatSetNoName_MeetUnderTheFixedNames_AndANamedHostDoesNotMeetThem()
    {
        await redis.FlushAsync();
        await using var unnamed = await StartHostAsync(null);
        await using var unnamedPeer = await StartHostAsync(null);
        await using var named = await StartHostAsync(ProductA);
        var atPeer = await ListenAsync(unnamedPeer);
        var atNamed = await ListenAsync(named);
        var requestId = Guid.CreateVersion7();

        unnamed.RequestReplay(requestId);
        Assert.True(unnamed.TryActivate(requestId));

        Assert.Equal(requestId, await atPeer.Task.WaitAsync(MessageLatency));
        Assert.True(await WaitUntilAsync(() => unnamedPeer.IsReplayActive, MessageLatency));
        Assert.True(await redis.Connection.GetDatabase().KeyExistsAsync("stratara:projection:replay:active"));
        await Task.Delay(SilenceWindow);
        Assert.False(atNamed.Task.IsCompleted);
        Assert.False(named.IsReplayActive);
    }

    private IReadOnlyList<string> KeysInTheStore() =>
        redis.Connection.GetServer(redis.Connection.GetEndPoints()[0]).Keys(pattern: "*").Select(key => key.ToString()).ToList();
}
