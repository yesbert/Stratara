using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Stratara.Abstractions.Messaging;
using Stratara.Diagnostics;
using Stratara.Outbox.RabbitMQ.Messaging;
using Stratara.Shared.Messaging;
using Testcontainers.RabbitMq;

namespace Stratara.Outbox.RabbitMQ.IntegrationTests.Messaging;

/// <summary>
/// What a subscription keeps across a real broker restart. The same container is stopped and started, so what the
/// broker wrote durably survives and what it holds only in memory does not. Each test runs a broker of its own because
/// it restarts it, on a host port fixed for the container's life: Docker hands out a new random port on every start,
/// and the clients under test recover against the address they first connected to.
/// </summary>
public sealed class RabbitMqBrokerRestartTests : IAsyncLifetime
{
    private const int AmqpPort = 5672;
    private static readonly IHostEnvironment DevHostEnv = new TestHostEnv();
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly RabbitMqContainer _broker = new RabbitMqBuilder("rabbitmq:4-management-alpine")
        .WithPortBinding(FreeHostPort(), AmqpPort)
        .Build();

    private readonly RecordingBusLogger _logger = new();
    private readonly List<RabbitMqBus> _buses = [];
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(4));

    public sealed record TestMessage(string Payload);

    private sealed class TestHostEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Stratara.Outbox.RabbitMQ.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public async ValueTask InitializeAsync() => await _broker.StartAsync(_timeout.Token);

    public async ValueTask DisposeAsync()
    {
        await _timeout.CancelAsync();
        foreach (var bus in _buses)
        {
            await bus.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }

        await _broker.DisposeAsync();
        _timeout.Dispose();
    }

    /// <summary>
    /// The finding: a subscription established ahead of its handler stays established over a restart. Its queue always
    /// survived; what did not was the exchange it is bound to, and with it the binding. The subscription whose host is
    /// up rebinds on recovery, which is enough for the broker to report the publication as routed, so nothing tells
    /// the publisher the other subscription missed it.
    /// </summary>
    [Fact]
    public async Task ASubscriptionEstablishedAheadOfItsHandler_StillReceivesWhatIsPublishedAfterARestart()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var hosted = $"worker-hosted-{Guid.NewGuid():N}";
        var absent = $"worker-absent-{Guid.NewGuid():N}";
        var bus = CreateBus();
        await bus.SubscribeAsync<TestMessage>(topic, hosted, _ => Task.CompletedTask, _timeout.Token);
        await bus.EnsureSubscriptionAsync(topic, absent, _timeout.Token);

        await RestartBrokerAsync();
        await PublishUntilAcceptedAsync(bus, topic, "after-the-restart");

        Assert.NotEqual(0u, await MessagesHeldAsync(RabbitMqBus.WorkerQueueName(absent)));

        var received = new TaskCompletionSource<TestMessage>();
        await CreateBus().SubscribeAsync<TestMessage>(topic, absent, message =>
        {
            received.TrySetResult(message);
            return Task.CompletedTask;
        }, _timeout.Token);
        Assert.Equal("after-the-restart", (await received.Task.WaitAsync(_timeout.Token)).Payload);
    }

    /// <summary>The same loss by the other road: the subscription had a handler, and its host is down over the restart.</summary>
    [Fact]
    public async Task ASubscriptionWhoseHostIsDownOverARestart_StillReceivesWhatIsPublishedAfterIt()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var hosted = $"worker-hosted-{Guid.NewGuid():N}";
        var absent = $"worker-absent-{Guid.NewGuid():N}";
        var bus = CreateBus();
        await bus.SubscribeAsync<TestMessage>(topic, hosted, _ => Task.CompletedTask, _timeout.Token);

        using (var hostAbsent = CancellationTokenSource.CreateLinkedTokenSource(_timeout.Token))
        {
            await using var absentHost = CreateUntrackedBus();
            await absentHost.SubscribeAsync<TestMessage>(topic, absent, _ => Task.CompletedTask, hostAbsent.Token);
            await hostAbsent.CancelAsync();
        }

        await RestartBrokerAsync();
        await PublishUntilAcceptedAsync(bus, topic, "after-the-restart");

        Assert.NotEqual(0u, await MessagesHeldAsync(RabbitMqBus.WorkerQueueName(absent)));
    }

    private RabbitMqBus CreateBus()
    {
        var bus = CreateUntrackedBus();
        _buses.Add(bus);
        return bus;
    }

    private RabbitMqBus CreateUntrackedBus()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:rabbitmq"] = _broker.GetConnectionString() })
            .Build();
        return new RabbitMqBus(_logger, configuration, DevHostEnv, Options.Create(new BusEnvelopeJsonOptions()), Options.Create(new MessageRetryOptions()));
    }

    private async Task RestartBrokerAsync()
    {
        await _broker.StopAsync(_timeout.Token);
        await _broker.StartAsync(_timeout.Token);
        await WaitUntilBrokerAnswersAsync();
    }

    private async Task WaitUntilBrokerAnswersAsync()
    {
        while (true)
        {
            try
            {
                await using var connection = await OpenConnectionAsync();
                return;
            }
            catch (Exception ex) when (ex is BrokerUnreachableException or OperationInterruptedException or IOException or SocketException)
            {
                await Task.Delay(PollInterval, _timeout.Token);
            }
        }
    }

    /// <summary>
    /// Publishes until the broker takes the message. Between the restart and the recovery of the hosted subscription
    /// the publication can be returned, which is the outbox's case and not what these tests look at.
    /// </summary>
    private async Task PublishUntilAcceptedAsync(RabbitMqBus bus, string topic, string payload)
    {
        while (true)
        {
            try
            {
                await bus.PublishAsync(topic, new TestMessage(payload), _timeout.Token);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Task.Delay(PollInterval, _timeout.Token);
            }
        }
    }

    private async Task<uint> MessagesHeldAsync(string queue)
    {
        await using var connection = await OpenConnectionAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: _timeout.Token);
        return (await channel.QueueDeclarePassiveAsync(queue, _timeout.Token)).MessageCount;
    }

    private Task<IConnection> OpenConnectionAsync() =>
        new ConnectionFactory { Uri = new Uri(_broker.GetConnectionString()) }.CreateConnectionAsync(_timeout.Token);

    private static int FreeHostPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class RecordingBusLogger : ILogger<RabbitMqBus>
    {
        private readonly ConcurrentQueue<(LogLevel Level, int EventId, string Message)> _entries = new();

        public IReadOnlyCollection<(LogLevel Level, int EventId, string Message)> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, eventId.Id, formatter(state, exception)));
    }
}
