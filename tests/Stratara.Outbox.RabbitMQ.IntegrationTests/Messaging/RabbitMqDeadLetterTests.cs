using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Messaging;
using Stratara.Diagnostics;
using Stratara.Outbox.RabbitMQ.IntegrationTests.Fixtures;
using Stratara.Outbox.RabbitMQ.Messaging;

namespace Stratara.Outbox.RabbitMQ.IntegrationTests.Messaging;

/// <summary>
/// The scenarios of <c>outbox-and-messaging</c> → <em>A message a handler cannot take is retried a
/// bounded number of times and then kept</em>, against a live RabbitMQ 4 broker: a worker
/// subscription is a quorum queue with a dead-letter queue beside it, the framework applies the
/// bounds from the broker's delivery count, and an operator can return a dead-lettered message.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public sealed class RabbitMqDeadLetterTests(RabbitMqFixture fixture)
{
    private static readonly IHostEnvironment DevHostEnv = new TestHostEnv();
    private static readonly TimeSpan DeadLetterWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(2);

    private sealed class TestHostEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Stratara.Outbox.RabbitMQ.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public sealed record TestMessage(string Payload);

    private RabbitMqBus CreateBus(MessageRetryOptions retry) =>
        new(NullLogger<RabbitMqBus>.Instance, fixture.Configuration, DevHostEnv, Options.Create(new BusEnvelopeJsonOptions()), Options.Create(retry));

    [Fact]
    public async Task HandlerKeepsFailing_MessageIsDeadLetteredAfterTheAttemptBound_AndCounted()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var subscription = $"worker-{Guid.NewGuid():N}";
        var bus = CreateBus(new MessageRetryOptions { MaxDeliveryAttempts = 3 });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var meter = MeterCapture.Start(subscription);

        var attempts = 0;
        await bus.SubscribeAsync<TestMessage>(topic, subscription, _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("poison");
        }, cts.Token);
        await Task.Delay(200, cts.Token);

        await bus.PublishAsync(topic, new TestMessage("poison"), cts.Token);

        var deadLettered = await WaitForDeadLetterAsync(subscription, cts.Token);
        Assert.Equal("poison", deadLettered.Payload);
        Assert.Equal(3, attempts);
        Assert.Equal(1, meter.Count(reason: "failure"));
    }

    [Fact]
    public async Task HandlerFailsOnce_MessageIsAcknowledgedOnTheNextDelivery_NotDeadLettered()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var subscription = $"worker-{Guid.NewGuid():N}";
        var bus = CreateBus(new MessageRetryOptions { MaxDeliveryAttempts = 3 });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var attempts = 0;
        var succeeded = new TaskCompletionSource();
        await bus.SubscribeAsync<TestMessage>(topic, subscription, _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("transient");
            }

            succeeded.TrySetResult();
            return Task.CompletedTask;
        }, cts.Token);
        await Task.Delay(200, cts.Token);

        await bus.PublishAsync(topic, new TestMessage("once"), cts.Token);

        await succeeded.Task.WaitAsync(cts.Token);
        await Task.Delay(QuietPeriod, cts.Token);
        Assert.Equal(2, attempts);
        Assert.Null(await TryGetDeadLetterAsync(subscription, cts.Token));
    }

    /// <summary>
    /// A handler whose save committed its events but could not publish them is not run again: a second
    /// delivery would record the same facts twice. The message is acknowledged, not redelivered and not
    /// dead-lettered.
    /// </summary>
    [Fact]
    public async Task HandlerCommittedButCouldNotPublish_MessageIsAcknowledged_NotRedeliveredOrDeadLettered()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var subscription = $"worker-{Guid.NewGuid():N}";
        var bus = CreateBus(new MessageRetryOptions { MaxDeliveryAttempts = 3 });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var subscribed = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        var attempts = 0;
        var handled = new TaskCompletionSource();
        await bus.SubscribeAsync<TestMessage>(topic, subscription, _ =>
        {
            Interlocked.Increment(ref attempts);
            handled.TrySetResult();
            throw new CommittedEventsNotPublishedException([Guid.NewGuid()], 1, new InvalidOperationException("the outbox table is down"));
        }, subscribed.Token);
        await Task.Delay(200, cts.Token);

        await bus.PublishAsync(topic, new TestMessage("committed"), cts.Token);

        await handled.Task.WaitAsync(cts.Token);
        await Task.Delay(QuietPeriod, cts.Token);
        Assert.Equal(1, attempts);
        Assert.Null(await TryGetDeadLetterAsync(subscription, cts.Token));

        // Closing the consumer returns a message it never settled to the queue; an acknowledged one is gone.
        await subscribed.CancelAsync();
        await Task.Delay(QuietPeriod, cts.Token);
        await using var connection = await new ConnectionFactory { Uri = new Uri(fixture.ConnectionString) }.CreateConnectionAsync(cts.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);
        Assert.Equal(0u, await channel.MessageCountAsync(RabbitMqBus.WorkerQueueName(subscription), cts.Token));
    }

    /// <summary>
    /// The subscription stops while its handler runs — the host is shutting down — and the handler still settles: a
    /// handler that completed, or one whose save committed but could not publish, has done its work, and an
    /// acknowledgement cancelled with the subscription would hand the message back to run it again.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscriptionStopsDuringTheHandler_TheMessageIsStillAcknowledged(bool committedButNotPublished)
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var subscription = $"worker-{Guid.NewGuid():N}";
        var bus = CreateBus(new MessageRetryOptions { MaxDeliveryAttempts = 3 });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var subscribed = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        var handled = new TaskCompletionSource();
        await bus.SubscribeAsync<TestMessage>(topic, subscription, async _ =>
        {
            await subscribed.CancelAsync();
            handled.TrySetResult();
            if (committedButNotPublished)
            {
                throw new CommittedEventsNotPublishedException([Guid.NewGuid()], 1, new InvalidOperationException("the outbox table is down"));
            }
        }, subscribed.Token);
        await Task.Delay(200, cts.Token);

        await bus.PublishAsync(topic, new TestMessage("stopping"), cts.Token);

        await handled.Task.WaitAsync(cts.Token);
        await Task.Delay(QuietPeriod, cts.Token);
        await using var connection = await new ConnectionFactory { Uri = new Uri(fixture.ConnectionString) }.CreateConnectionAsync(cts.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);
        Assert.Equal(0u, await channel.MessageCountAsync(RabbitMqBus.WorkerQueueName(subscription), cts.Token));
    }

    [Fact]
    public async Task HandlerKeepsConflicting_MessageIsDeadLetteredPastTheRequeueBound_AsAConflict()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var subscription = $"worker-{Guid.NewGuid():N}";
        var bus = CreateBus(new MessageRetryOptions { MaxDeliveryAttempts = 1, MaxConflictRequeues = 4 });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var meter = MeterCapture.Start(subscription);

        var attempts = 0;
        await bus.SubscribeAsync<TestMessage>(topic, subscription, _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new ConcurrencyException(Guid.NewGuid(), "TestAggregate");
        }, cts.Token);
        await Task.Delay(200, cts.Token);

        await bus.PublishAsync(topic, new TestMessage("contended"), cts.Token);

        var deadLettered = await WaitForDeadLetterAsync(subscription, cts.Token);
        Assert.Equal("contended", deadLettered.Payload);
        Assert.Equal(5, attempts);
        Assert.Equal(1, meter.Count(reason: "conflict"));
        Assert.Equal(0, meter.Count(reason: "failure"));
    }

    [Fact]
    public async Task OperatorReturnsADeadLetteredMessage_ItIsDeliveredAgainWithTheCountStartingOver()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var subscription = $"worker-{Guid.NewGuid():N}";
        var bus = CreateBus(new MessageRetryOptions { MaxDeliveryAttempts = 2 });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var attempts = 0;
        await bus.SubscribeAsync<TestMessage>(topic, subscription, _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("still poison");
        }, cts.Token);
        await Task.Delay(200, cts.Token);

        await bus.PublishAsync(topic, new TestMessage("returned"), cts.Token);
        var first = await WaitForDeadLetterRawAsync(subscription, cts.Token);
        Assert.Equal(2, attempts);

        await ReturnToSubscriptionAsync(subscription, first, cts.Token);

        var second = await WaitForDeadLetterAsync(subscription, cts.Token);
        Assert.Equal("returned", second.Payload);
        Assert.Equal(4, attempts);
    }

    /// <summary>
    /// <em>A host configures the bounds</em>, on a broker where the subscription already exists
    /// under the bounds of an earlier deployment: the queue keeps what it was declared with, and the
    /// subscription must open anyway rather than fail its redeclaration.
    /// </summary>
    [Fact]
    public async Task BoundsChangeOnAnExistingSubscription_TheSubscriptionStillOpens_AndTheNewBoundsApply()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var subscription = $"worker-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var earlierDeployment = CreateBus(new MessageRetryOptions { MaxDeliveryAttempts = 3, MaxConflictRequeues = 4 });
        await earlierDeployment.EnsureSubscriptionAsync(topic, subscription, cts.Token);

        var bus = CreateBus(new MessageRetryOptions { MaxDeliveryAttempts = 2, MaxConflictRequeues = 7 });
        await bus.EnsureSubscriptionAsync(topic, subscription, cts.Token);

        var attempts = 0;
        await bus.SubscribeAsync<TestMessage>(topic, subscription, _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("poison");
        }, cts.Token);
        await Task.Delay(200, cts.Token);

        await bus.PublishAsync(topic, new TestMessage("rebounded"), cts.Token);

        var deadLettered = await WaitForDeadLetterAsync(subscription, cts.Token);
        Assert.Equal("rebounded", deadLettered.Payload);
        Assert.Equal(2, attempts);
    }

    /// <summary>
    /// The tolerance for an earlier deployment's delivery limit stops there. A worker queue of the
    /// right name but the wrong type has no dead-letter route, so a message a handler cannot take
    /// would be dropped; the subscription must refuse to open on it.
    /// </summary>
    [Fact]
    public async Task AClassicQueueUnderTheWorkerQueueName_StillFailsTheSubscription()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var subscription = $"worker-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using (var connection = await new ConnectionFactory { Uri = new Uri(fixture.ConnectionString) }.CreateConnectionAsync(cts.Token))
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: cts.Token))
        {
            await channel.QueueDeclareAsync(RabbitMqBus.WorkerQueueName(subscription), durable: true, exclusive: false, autoDelete: false, cancellationToken: cts.Token);
        }

        var bus = CreateBus(new MessageRetryOptions());

        var refused = await Assert.ThrowsAsync<global::RabbitMQ.Client.Exceptions.OperationInterruptedException>(
            () => bus.EnsureSubscriptionAsync(topic, subscription, cts.Token));
        Assert.Equal((ushort?)406, refused.ShutdownReason?.ReplyCode);
        Assert.DoesNotContain("'x-delivery-limit'", refused.ShutdownReason!.ReplyText, StringComparison.Ordinal);
    }

    private sealed record DeadLettered(byte[] Body, IReadOnlyBasicProperties Properties);

    private async Task<TestMessage> WaitForDeadLetterAsync(string subscription, CancellationToken cancellationToken)
    {
        var raw = await WaitForDeadLetterRawAsync(subscription, cancellationToken);
        return JsonSerializer.Deserialize<TestMessage>(raw.Body) ?? throw new InvalidOperationException("The dead-lettered body did not deserialise.");
    }

    private async Task<DeadLettered> WaitForDeadLetterRawAsync(string subscription, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + DeadLetterWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var message = await TryGetDeadLetterAsync(subscription, cancellationToken);
            if (message is not null)
            {
                return message;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException($"No message reached {RabbitMqBus.DeadLetterQueueName(subscription)} within {DeadLetterWait}.");
    }

    private async Task<DeadLettered?> TryGetDeadLetterAsync(string subscription, CancellationToken cancellationToken)
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(fixture.ConnectionString) }.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        var result = await channel.BasicGetAsync(RabbitMqBus.DeadLetterQueueName(subscription), autoAck: true, cancellationToken);
        return result is null ? null : new DeadLettered(result.Body.ToArray(), result.BasicProperties);
    }

    /// <summary>
    /// What a shovel or the management UI does: republishes the message to the worker queue with the
    /// properties it carried on the dead-letter queue — including the delivery count of its earlier
    /// life, which the consumer must ignore on a first delivery.
    /// </summary>
    private async Task ReturnToSubscriptionAsync(string subscription, DeadLettered message, CancellationToken cancellationToken)
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(fixture.ConnectionString) }.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.BasicPublishAsync(string.Empty, RabbitMqBus.WorkerQueueName(subscription), false, new BasicProperties(message.Properties), message.Body, cancellationToken);
    }

    /// <summary>Counts <c>messaging.dead_lettered</c> measurements for one subscription; the meter is process-global, so the filter is the subscription the test owns.</summary>
    private sealed class MeterCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<string> _reasons = [];
        private readonly Lock _gate = new();

        private MeterCapture(string subscription)
        {
            // Read before the listener starts: the first read initialises the metrics, and an initialisation
            // that publishes its instruments into a running listener would call back into it half-built.
            var meterName = ApplicationDiagnostics.Metrics.MeterName;
            var instrumentName = ApplicationDiagnostics.Metrics.MessagesDeadLettered.Name;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == meterName && instrument.Name == instrumentName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                string? taggedSubscription = null;
                string? reason = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == ApplicationDiagnostics.MetricTags.Subscription)
                    {
                        taggedSubscription = tag.Value as string;
                    }
                    else if (tag.Key == ApplicationDiagnostics.MetricTags.Reason)
                    {
                        reason = tag.Value as string;
                    }
                }

                if (taggedSubscription == subscription && reason is not null)
                {
                    lock (_gate)
                    {
                        for (var i = 0; i < value; i++)
                        {
                            _reasons.Add(reason);
                        }
                    }
                }
            });
            _listener.Start();
        }

        public static MeterCapture Start(string subscription) => new(subscription);

        public int Count(string reason)
        {
            lock (_gate)
            {
                return _reasons.Count(r => r == reason);
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
