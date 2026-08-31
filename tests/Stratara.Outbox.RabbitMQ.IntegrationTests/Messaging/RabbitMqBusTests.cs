using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client.Exceptions;
using Stratara.Outbox.RabbitMQ.Messaging;
using Stratara.Outbox.RabbitMQ.IntegrationTests.Fixtures;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Messaging;

namespace Stratara.Outbox.RabbitMQ.IntegrationTests.Messaging;

[Collection(RabbitMqCollection.Name)]
public sealed class RabbitMqBusTests(RabbitMqFixture fixture)
{
    private static readonly IHostEnvironment DevHostEnv = new TestHostEnv();

    private sealed class TestHostEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Stratara.Outbox.RabbitMQ.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public sealed record TestMessage(string Payload);

    [Fact]
    public async Task PublishAsync_RoundtripsToSubscriber()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var subscription = $"default-{Guid.NewGuid():N}";
        var bus = new RabbitMqBus(NullLogger<RabbitMqBus>.Instance, fixture.Configuration, DevHostEnv, Options.Create(new BusEnvelopeJsonOptions()));

        var received = new TaskCompletionSource<TestMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await bus.SubscribeAsync<TestMessage>(topic, subscription, msg =>
        {
            received.TrySetResult(msg);
            return Task.CompletedTask;
        }, cts.Token);

        await Task.Delay(200, cts.Token);
        await bus.PublishAsync(topic, new TestMessage("hello"), cts.Token);

        var result = await received.Task.WaitAsync(cts.Token);
        Assert.Equal("hello", result.Payload);
    }

    /// <summary>
    /// The defect this change exists to remove, pinned as it behaves today: on a topic with more
    /// than one subscription, a subscription that binds after a publication misses it — and the
    /// publisher is told nothing, because the subscription that was already bound is enough for the
    /// broker to consider the message routed. A single-subscription version of this test raises a
    /// return and proves the opposite.
    /// </summary>
    [Fact]
    public async Task PublishAsync_WhenASecondSubscriptionBindsLate_ItMissesTheMessageAndPublishStaysSilent()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var boundEarly = $"worker-early-{Guid.NewGuid():N}";
        var boundLate = $"worker-late-{Guid.NewGuid():N}";
        var bus = new RabbitMqBus(NullLogger<RabbitMqBus>.Instance, fixture.Configuration, DevHostEnv, Options.Create(new BusEnvelopeJsonOptions()));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var early = new TaskCompletionSource<TestMessage>();
        var late = new TaskCompletionSource<TestMessage>();

        await bus.SubscribeAsync<TestMessage>(topic, boundEarly, msg =>
        {
            early.TrySetResult(msg);
            return Task.CompletedTask;
        }, cts.Token);
        await Task.Delay(200, cts.Token);

        // No exception here is half the finding: the early subscription is enough for the broker to
        // route it, so nothing tells the publisher that the late one did not exist.
        await bus.PublishAsync(topic, new TestMessage("only-the-early-one-sees-this"), cts.Token);

        var seenByEarly = await early.Task.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);
        Assert.Equal("only-the-early-one-sees-this", seenByEarly.Payload);

        await bus.SubscribeAsync<TestMessage>(topic, boundLate, msg =>
        {
            late.TrySetResult(msg);
            return Task.CompletedTask;
        }, cts.Token);

        var deliveredToLate = await Task.WhenAny(late.Task, Task.Delay(TimeSpan.FromSeconds(5), cts.Token));
        Assert.NotSame(late.Task, deliveredToLate);
        Assert.False(late.Task.IsCompleted);
    }

    /// <summary>
    /// The counterpart, and the point of the change: the same ordering as the test above, except
    /// the second subscription is established before the publication rather than after it. It then
    /// receives what it would otherwise have missed, once its handler attaches.
    /// </summary>
    [Fact]
    public async Task EnsureSubscriptionAsync_EstablishedBeforePublish_DeliversOnceTheHandlerAttaches()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var boundEarly = $"worker-early-{Guid.NewGuid():N}";
        var establishedEarly = $"worker-established-{Guid.NewGuid():N}";
        var bus = new RabbitMqBus(NullLogger<RabbitMqBus>.Instance, fixture.Configuration, DevHostEnv, Options.Create(new BusEnvelopeJsonOptions()));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var early = new TaskCompletionSource<TestMessage>();
        var established = new TaskCompletionSource<TestMessage>();

        await bus.SubscribeAsync<TestMessage>(topic, boundEarly, msg =>
        {
            early.TrySetResult(msg);
            return Task.CompletedTask;
        }, cts.Token);

        await bus.EnsureSubscriptionAsync(topic, establishedEarly, cts.Token);
        await Task.Delay(200, cts.Token);

        await bus.PublishAsync(topic, new TestMessage("both-must-see-this"), cts.Token);

        var seenByEarly = await early.Task.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);
        Assert.Equal("both-must-see-this", seenByEarly.Payload);

        await bus.SubscribeAsync<TestMessage>(topic, establishedEarly, msg =>
        {
            established.TrySetResult(msg);
            return Task.CompletedTask;
        }, cts.Token);

        var seenByEstablished = await established.Task.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);
        Assert.Equal("both-must-see-this", seenByEstablished.Payload);
    }

    /// <summary>
    /// A client subscription is exclusive and auto-deleting, so a queue established for it ahead of
    /// its consumer would be gone before the handler attached. Refusing is the honest answer; a
    /// no-op would be indistinguishable from having worked.
    /// </summary>
    [Fact]
    public async Task EnsureSubscriptionAsync_ForAClientSubscription_IsRefused()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var clientSubscription = $"default-{Guid.NewGuid():N}";
        var bus = new RabbitMqBus(NullLogger<RabbitMqBus>.Instance, fixture.Configuration, DevHostEnv, Options.Create(new BusEnvelopeJsonOptions()));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bus.EnsureSubscriptionAsync(topic, clientSubscription, cts.Token));

        Assert.Contains("auto-deleting", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscribeAsync_HandlerSucceeds_MessageIsAcked()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var subscription = $"default-{Guid.NewGuid():N}";
        var bus = new RabbitMqBus(NullLogger<RabbitMqBus>.Instance, fixture.Configuration, DevHostEnv, Options.Create(new BusEnvelopeJsonOptions()));

        var processed = 0;
        var firstReceived = new TaskCompletionSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await bus.SubscribeAsync<TestMessage>(topic, subscription, _ =>
        {
            if (Interlocked.Increment(ref processed) == 1)
            {
                firstReceived.TrySetResult();
            }
            return Task.CompletedTask;
        }, cts.Token);

        await Task.Delay(200, cts.Token);
        await bus.PublishAsync(topic, new TestMessage("one"), cts.Token);
        await firstReceived.Task.WaitAsync(cts.Token);
        await Task.Delay(500, cts.Token);

        Assert.Equal(1, processed);
    }

    [Fact]
    public async Task SubscribeAsync_HandlerThrowsConcurrencyException_MessageIsRequeued()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var subscription = $"worker-{Guid.NewGuid():N}";
        var bus = new RabbitMqBus(NullLogger<RabbitMqBus>.Instance, fixture.Configuration, DevHostEnv, Options.Create(new BusEnvelopeJsonOptions()));

        var attempts = 0;
        var secondAttempt = new TaskCompletionSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await bus.SubscribeAsync<TestMessage>(topic, subscription, _ =>
        {
            var count = Interlocked.Increment(ref attempts);
            if (count == 1)
            {
                throw new ConcurrencyException(Guid.NewGuid(), "TestAggregate");
            }
            secondAttempt.TrySetResult();
            return Task.CompletedTask;
        }, cts.Token);

        await Task.Delay(200, cts.Token);
        await bus.PublishAsync(topic, new TestMessage("retry-me"), cts.Token);

        await secondAttempt.Task.WaitAsync(cts.Token);
        Assert.True(attempts >= 2);
    }

    [Fact]
    public async Task SubscribeAsync_HandlerThrowsGenericException_MessageIsNotRequeued()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var subscription = $"worker-{Guid.NewGuid():N}";
        var bus = new RabbitMqBus(NullLogger<RabbitMqBus>.Instance, fixture.Configuration, DevHostEnv, Options.Create(new BusEnvelopeJsonOptions()));

        var attempts = 0;
        var firstAttempt = new TaskCompletionSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await bus.SubscribeAsync<TestMessage>(topic, subscription, _ =>
        {
            Interlocked.Increment(ref attempts);
            firstAttempt.TrySetResult();
            throw new InvalidOperationException("poison message");
        }, cts.Token);

        await Task.Delay(200, cts.Token);
        await bus.PublishAsync(topic, new TestMessage("poison"), cts.Token);

        await firstAttempt.Task.WaitAsync(cts.Token);
        await Task.Delay(1000, cts.Token);

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task PublishAsync_NoSubscriberBound_ThrowsPublishReturnException()
    {
        // KI-02 regression-anchor: with mandatory=true, publishing to a fanout exchange that has
        // no queues bound must surface as PublishReturnException so EventBundleOutboxDispatcher /
        // CommandOutboxDispatcher fall back to the outbox table instead of silently dropping the
        // message.
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var bus = new RabbitMqBus(NullLogger<RabbitMqBus>.Instance, fixture.Configuration, DevHostEnv, Options.Create(new BusEnvelopeJsonOptions()));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var thrown = await Assert.ThrowsAnyAsync<PublishException>(
            () => bus.PublishAsync(topic, new TestMessage("dropped"), cts.Token));

        Assert.True(thrown.IsReturn);
    }

    [Fact]
    public async Task PublishAsync_PersistsMessageAcrossSubscribeOrder()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var subscription = $"worker-{Guid.NewGuid():N}";
        var bus = new RabbitMqBus(NullLogger<RabbitMqBus>.Instance, fixture.Configuration, DevHostEnv, Options.Create(new BusEnvelopeJsonOptions()));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Worker-subscription with durable queue: subscribe FIRST to declare the queue,
        // disconnect via cancellation, publish, then re-subscribe — message must still arrive.
        using var subscribeCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var firstReady = new TaskCompletionSource();
        await bus.SubscribeAsync<TestMessage>(topic, subscription, _ =>
        {
            firstReady.TrySetResult();
            return Task.CompletedTask;
        }, subscribeCts.Token);
        await Task.Delay(300, cts.Token);

        await subscribeCts.CancelAsync();
        await Task.Delay(300, cts.Token);

        await bus.PublishAsync(topic, new TestMessage("durable"), cts.Token);

        var received = new TaskCompletionSource<TestMessage>();
        await bus.SubscribeAsync<TestMessage>(topic, subscription, msg =>
        {
            received.TrySetResult(msg);
            return Task.CompletedTask;
        }, cts.Token);

        var result = await received.Task.WaitAsync(cts.Token);
        Assert.Equal("durable", result.Payload);
    }
}
