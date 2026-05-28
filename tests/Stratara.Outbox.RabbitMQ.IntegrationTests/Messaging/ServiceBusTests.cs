using Azure.Messaging.ServiceBus;
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
        var bus = new SutServiceBus(NullLogger<SutServiceBus>.Instance, _client, Options.Create(new BusEnvelopeJsonOptions()));

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
        var bus = new SutServiceBus(NullLogger<SutServiceBus>.Instance, _client, Options.Create(new BusEnvelopeJsonOptions()));

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
        var bus = new SutServiceBus(NullLogger<SutServiceBus>.Instance, _client, Options.Create(new BusEnvelopeJsonOptions()));

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

    [Fact]
    public async Task SubscribeAsync_HandlerThrowsGenericException_MessageIsDeadLettered()
    {
        var bus = new SutServiceBus(NullLogger<SutServiceBus>.Instance, _client, Options.Create(new BusEnvelopeJsonOptions()));

        var attempts = 0;
        var firstAttempt = new TaskCompletionSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await bus.SubscribeAsync<TestMessage>("test-deadletter", "worker", _ =>
        {
            Interlocked.Increment(ref attempts);
            firstAttempt.TrySetResult();
            throw new InvalidOperationException("poison message");
        }, cts.Token);

        await Task.Delay(500, cts.Token);
        await bus.PublishAsync("test-deadletter", new TestMessage("poison"), cts.Token);

        await firstAttempt.Task.WaitAsync(cts.Token);
        await Task.Delay(2000, cts.Token);

        Assert.Equal(1, attempts);

        await using var dlqReceiver = _client.CreateReceiver("test-deadletter", "worker", new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
        });
        var dlqMessage = await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.NotNull(dlqMessage);
        Assert.Equal(nameof(InvalidOperationException), dlqMessage.DeadLetterReason);
    }

    [Fact]
    public async Task PublishAsync_PersistsMessageAcrossSubscribeOrder()
    {
        var bus = new SutServiceBus(NullLogger<SutServiceBus>.Instance, _client, Options.Create(new BusEnvelopeJsonOptions()));

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
