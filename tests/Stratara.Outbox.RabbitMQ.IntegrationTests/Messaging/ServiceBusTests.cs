using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stratara.Outbox.RabbitMQ.IntegrationTests.Fixtures;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Messaging;
using SutServiceBus = Stratara.Outbox.AzureServiceBus.Messaging.AzureServiceBusBus;

namespace Stratara.Outbox.RabbitMQ.IntegrationTests.Messaging;

[Collection(ServiceBusCollection.Name)]
public sealed class ServiceBusTests(ServiceBusFixture fixture) : IAsyncDisposable
{
    public sealed record TestMessage(string Payload);

    private readonly ServiceBusClient _client = new(fixture.ConnectionString);

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_RoundtripsToSubscriber()
    {
        var bus = new SutServiceBus(NullLogger<SutServiceBus>.Instance, _client, Options.Create(new BusEnvelopeJsonOptions()), Options.Create(new MessageRetryOptions()));

        var received = new TaskCompletionSource<TestMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await bus.SubscribeAsync<TestMessage>("test-roundtrip", "default", msg =>
        {
            received.TrySetResult(msg);
            return Task.CompletedTask;
        }, cts.Token);

        await Task.Delay(500, cts.Token);
        await bus.PublishAsync("test-roundtrip", new TestMessage("hello"), cts.Token);

        var result = await received.Task.WaitAsync(cts.Token);
        Assert.Equal("hello", result.Payload);
    }

    [Fact]
    public async Task SubscribeAsync_HandlerSucceeds_MessageIsCompleted()
    {
        var bus = new SutServiceBus(NullLogger<SutServiceBus>.Instance, _client, Options.Create(new BusEnvelopeJsonOptions()), Options.Create(new MessageRetryOptions()));

        var processed = 0;
        var firstReceived = new TaskCompletionSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await bus.SubscribeAsync<TestMessage>("test-ack", "default", _ =>
        {
            if (Interlocked.Increment(ref processed) == 1)
            {
                firstReceived.TrySetResult();
            }
            return Task.CompletedTask;
        }, cts.Token);

        await Task.Delay(500, cts.Token);
        await bus.PublishAsync("test-ack", new TestMessage("one"), cts.Token);
        await firstReceived.Task.WaitAsync(cts.Token);
        await Task.Delay(2000, cts.Token);

        Assert.Equal(1, processed);
    }

    [Fact]
    public async Task SubscribeAsync_HandlerThrowsConcurrencyException_MessageIsAbandoned()
    {
        var bus = new SutServiceBus(NullLogger<SutServiceBus>.Instance, _client, Options.Create(new BusEnvelopeJsonOptions()), Options.Create(new MessageRetryOptions()));

        var attempts = 0;
        var secondAttempt = new TaskCompletionSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        await bus.SubscribeAsync<TestMessage>("test-concurrency-requeue", "worker", _ =>
        {
            var count = Interlocked.Increment(ref attempts);
            if (count == 1)
            {
                throw new ConcurrencyException(Guid.NewGuid(), "TestAggregate");
            }
            secondAttempt.TrySetResult();
            return Task.CompletedTask;
        }, cts.Token);

        await Task.Delay(500, cts.Token);
        await bus.PublishAsync("test-concurrency-requeue", new TestMessage("retry-me"), cts.Token);

        await secondAttempt.Task.WaitAsync(cts.Token);
        Assert.True(attempts >= 2, $"Expected at least 2 delivery attempts, got {attempts}.");
    }

    /// <summary>
    /// A handler whose save committed its events but could not publish them is not run again: a second
    /// delivery would record the same facts twice. The message is completed, not abandoned or dead-lettered.
    /// </summary>
    [Fact]
    public async Task SubscribeAsync_HandlerCommittedButCouldNotPublish_MessageIsCompleted()
    {
        var bus = new SutServiceBus(NullLogger<SutServiceBus>.Instance, _client, Options.Create(new BusEnvelopeJsonOptions()), Options.Create(new MessageRetryOptions { MaxDeliveryAttempts = 3 }));

        var attempts = 0;
        var handled = new TaskCompletionSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await bus.SubscribeAsync<TestMessage>("test-committed-not-published", "worker", _ =>
        {
            Interlocked.Increment(ref attempts);
            handled.TrySetResult();
            throw new CommittedEventsNotPublishedException([Guid.NewGuid()], 1, new InvalidOperationException("the outbox table is down"));
        }, cts.Token);

        await Task.Delay(500, cts.Token);
        await bus.PublishAsync("test-committed-not-published", new TestMessage("committed"), cts.Token);
        await handled.Task.WaitAsync(cts.Token);
        await Task.Delay(3000, cts.Token);

        Assert.Equal(1, attempts);
        await using var dlqReceiver = _client.CreateReceiver("test-committed-not-published", "worker", new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
        });
        Assert.Null(await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2), cts.Token));
        await using var activeReceiver = _client.CreateReceiver("test-committed-not-published", "worker");
        Assert.Null(await activeReceiver.PeekMessageAsync(cancellationToken: cts.Token));
    }

    /// <summary>
    /// The subscription stops while its handler runs — the host is shutting down. The handler settles its message, and
    /// the stopped processor takes no message published afterwards.
    /// </summary>
    [Fact]
    public async Task SubscribeAsync_SubscriptionStopsDuringTheHandler_TheMessageIsCompletedAndNoMoreAreTaken()
    {
        await using var bus = new SutServiceBus(NullLogger<SutServiceBus>.Instance, _client, Options.Create(new BusEnvelopeJsonOptions()), Options.Create(new MessageRetryOptions { MaxDeliveryAttempts = 3 }));

        var handled = 0;
        var first = new TaskCompletionSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var subscribed = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        await bus.SubscribeAsync<TestMessage>("test-stopping-subscription", "worker", async _ =>
        {
            Interlocked.Increment(ref handled);
            await subscribed.CancelAsync();
            first.TrySetResult();
        }, subscribed.Token);

        await Task.Delay(500, cts.Token);
        await bus.PublishAsync("test-stopping-subscription", new TestMessage("first"), cts.Token);
        await first.Task.WaitAsync(cts.Token);
        await bus.DisposeAsync();

        await bus.PublishAsync("test-stopping-subscription", new TestMessage("after the stop"), cts.Token);
        await Task.Delay(3000, cts.Token);

        Assert.Equal(1, handled);
        await using var receiver = _client.CreateReceiver("test-stopping-subscription", "worker");
        var waiting = await receiver.PeekMessageAsync(cancellationToken: cts.Token);
        Assert.NotNull(waiting);
        Assert.Contains("after the stop", waiting.Body.ToString(), StringComparison.Ordinal);
        Assert.Null(await receiver.PeekMessageAsync(waiting.SequenceNumber + 1, cts.Token));
    }

    /// <summary>
    /// The host stops while a handler runs. It waits for the handler to settle before it counts as stopped — so before
    /// the container that holds the handler's services is disposed — and the message is completed.
    /// </summary>
    [Fact]
    public async Task HostStopsDuringTheHandler_TheHostWaitsForItAndTheMessageIsCompleted()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var subscribed = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddAzureServiceBus(fixture.ConnectionString);
        // The emulator does not serve the administration endpoint; the bounds apply as configured.
        builder.Services.RemoveAll<ServiceBusAdministrationClient>();
        builder.Services.AddHostedService(provider => new SubscribingWorker(provider.GetRequiredService<IMessageBus>(), "test-host-stops", "worker", subscribed, async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        }));
        using var host = builder.Build();
        await host.StartAsync(cts.Token);
        await subscribed.Task.WaitAsync(cts.Token);

        await host.Services.GetRequiredService<IMessageBus>().PublishAsync("test-host-stops", new TestMessage("stopping"), cts.Token);
        await entered.Task.WaitAsync(cts.Token);

        var stopping = host.StopAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        Assert.False(stopping.IsCompleted);

        release.TrySetResult();
        await stopping.WaitAsync(cts.Token);
        await using var receiver = _client.CreateReceiver("test-host-stops", "worker");
        Assert.Null(await receiver.PeekMessageAsync(cancellationToken: cts.Token));
    }

    /// <summary>
    /// A handler that never returns cannot hold a stopping subscription — and the host's stop and disposal after it —
    /// up for longer than the bounded wait for the handler.
    /// </summary>
    [Fact]
    public async Task HandlerNeverReturns_StoppingTheSubscriptionStillFinishes()
    {
        var bus = new SutServiceBus(NullLogger<SutServiceBus>.Instance, _client, Options.Create(new BusEnvelopeJsonOptions()), Options.Create(new MessageRetryOptions { MaxDeliveryAttempts = 3 }));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var subscribed = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var entered = new TaskCompletionSource();
        var never = new TaskCompletionSource();
        await bus.SubscribeAsync<TestMessage>("test-stuck-handler", "worker", async _ =>
        {
            entered.TrySetResult();
            await never.Task;
        }, subscribed.Token);

        await Task.Delay(500, cts.Token);
        await bus.PublishAsync("test-stuck-handler", new TestMessage("stuck"), cts.Token);
        await entered.Task.WaitAsync(cts.Token);
        await subscribed.CancelAsync();

        try
        {
            await bus.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(40), cts.Token);
        }
        finally
        {
            never.TrySetResult();
        }
    }

    /// <summary>Subscribes when the host starts, with the host's stopping token — the way a worker does.</summary>
    private sealed class SubscribingWorker(IMessageBus bus, string topic, string subscription, TaskCompletionSource subscribed, Func<TestMessage, Task> handler) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await bus.SubscribeAsync(topic, subscription, handler, stoppingToken);
            subscribed.TrySetResult();
        }
    }

    /// <summary>
    /// <c>outbox-and-messaging</c> → <em>A handler keeps failing</em>, on the Service Bus emulator:
    /// the framework abandons the message until <c>MaxDeliveryAttempts</c> deliveries have failed
    /// and then dead-letters it itself, with the reason the operator filters on and the exception in
    /// the description.
    /// </summary>
    [Fact]
    public async Task SubscribeAsync_HandlerKeepsFailing_MessageIsDeadLetteredAfterTheAttemptBound()
    {
        var bus = new SutServiceBus(NullLogger<SutServiceBus>.Instance, _client, Options.Create(new BusEnvelopeJsonOptions()), Options.Create(new MessageRetryOptions { MaxDeliveryAttempts = 3 }));

        var attempts = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await bus.SubscribeAsync<TestMessage>("test-deadletter", "worker", _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("poison message");
        }, cts.Token);

        await Task.Delay(500, cts.Token);
        await bus.PublishAsync("test-deadletter", new TestMessage("poison"), cts.Token);

        await using var dlqReceiver = _client.CreateReceiver("test-deadletter", "worker", new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
        });
        var dlqMessage = await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30), cts.Token);
        Assert.NotNull(dlqMessage);
        Assert.Equal("failure", dlqMessage.DeadLetterReason);
        Assert.Contains(nameof(InvalidOperationException), dlqMessage.DeadLetterErrorDescription, StringComparison.Ordinal);
        Assert.Equal(3, attempts);
    }

    /// <summary>
    /// <c>outbox-and-messaging</c> → <em>A handler keeps conflicting</em>: a conflict is abandoned
    /// under <c>MaxConflictRequeues</c> and dead-lettered past it, as a conflict rather than a failure.
    /// </summary>
    [Fact]
    public async Task SubscribeAsync_HandlerKeepsConflicting_MessageIsDeadLetteredPastTheRequeueBound()
    {
        var bus = new SutServiceBus(NullLogger<SutServiceBus>.Instance, _client, Options.Create(new BusEnvelopeJsonOptions()), Options.Create(new MessageRetryOptions { MaxDeliveryAttempts = 1, MaxConflictRequeues = 2 }));

        var attempts = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await bus.SubscribeAsync<TestMessage>("test-conflict-deadletter", "worker", _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new ConcurrencyException(Guid.NewGuid(), "TestAggregate");
        }, cts.Token);

        await Task.Delay(500, cts.Token);
        await bus.PublishAsync("test-conflict-deadletter", new TestMessage("contended"), cts.Token);

        await using var dlqReceiver = _client.CreateReceiver("test-conflict-deadletter", "worker", new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
        });
        var dlqMessage = await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30), cts.Token);
        Assert.NotNull(dlqMessage);
        Assert.Equal("conflict", dlqMessage.DeadLetterReason);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task PublishAsync_PersistsMessageAcrossSubscribeOrder()
    {
        var bus = new SutServiceBus(NullLogger<SutServiceBus>.Instance, _client, Options.Create(new BusEnvelopeJsonOptions()), Options.Create(new MessageRetryOptions()));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await bus.PublishAsync("test-durable", new TestMessage("durable"), cts.Token);
        await Task.Delay(500, cts.Token);

        var received = new TaskCompletionSource<TestMessage>();
        await bus.SubscribeAsync<TestMessage>("test-durable", "worker", msg =>
        {
            received.TrySetResult(msg);
            return Task.CompletedTask;
        }, cts.Token);

        var result = await received.Task.WaitAsync(cts.Token);
        Assert.Equal("durable", result.Payload);
    }
}
