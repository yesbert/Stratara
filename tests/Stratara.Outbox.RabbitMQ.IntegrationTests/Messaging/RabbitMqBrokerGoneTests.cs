using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Messaging;
using Stratara.Diagnostics;
using Stratara.Outbox.RabbitMQ.Messaging;
using Stratara.Shared.Messaging;
using Testcontainers.RabbitMq;

namespace Stratara.Outbox.RabbitMQ.IntegrationTests.Messaging;

/// <summary>
/// The broker goes away before the subscriptions stop, as it does when a local stack stops its containers in no
/// particular order. A consumer whose connection is already closed is gone, which is what the stop wants: the stop
/// finishes without reporting a failed cleanup. A handler that completes after the broker went away cannot acknowledge
/// its message, and that is reported as a message the broker delivers again, not as a failed handler. Each test runs a
/// broker of its own because it stops it.
/// </summary>
public sealed class RabbitMqBrokerGoneTests : IAsyncLifetime
{
    private static readonly IHostEnvironment DevHostEnv = new TestHostEnv();
    private static readonly TimeSpan ClientNoticesTheClose = TimeSpan.FromSeconds(2);

    private readonly RabbitMqContainer _broker = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();
    private readonly LogCapture _logs = new();
    private ILoggerFactory _loggerFactory = null!;

    private sealed class TestHostEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Stratara.Outbox.RabbitMQ.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public sealed record TestMessage(string Payload);

    public async ValueTask InitializeAsync()
    {
        await _broker.StartAsync();
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(_logs));
    }

    public async ValueTask DisposeAsync()
    {
        _loggerFactory.Dispose();
        await _broker.DisposeAsync();
    }

    [Fact]
    public async Task BrokerGoneBeforeTheStop_TheSubscriptionsStopWithoutReportingAFailedCleanup()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var subscribed = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var bus = CreateBus();
        try
        {
            // A worker subscription and a client subscription, each on a connection of its own.
            await bus.SubscribeAsync<TestMessage>(topic, $"worker-{Guid.NewGuid():N}", _ => Task.CompletedTask, subscribed.Token);
            await bus.SubscribeAsync<TestMessage>(topic, $"default-{Guid.NewGuid():N}", _ => Task.CompletedTask, subscribed.Token);

            await _broker.StopAsync(cts.Token);
            await Task.Delay(ClientNoticesTheClose, cts.Token);

            await subscribed.CancelAsync();
        }
        finally
        {
            await bus.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }

        Assert.DoesNotContain(_logs.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Equal(2, _logs.Entries.Count(entry => entry.EventId == LogEvents.Messaging.SubscriptionCleanup));
        Assert.Equal(2, _logs.Entries.Count(entry => entry.EventId == LogEvents.Messaging.SubscriptionAlreadyClosed && entry.Level == LogLevel.Debug));
    }

    [Fact]
    public async Task BrokerGoneWhileAHandlerRuns_TheMessageIsReportedAsDeliveredAgainRatherThanAsAFailure()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var subscribed = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var running = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var bus = CreateBus();
        try
        {
            await bus.SubscribeAsync<TestMessage>(topic, $"worker-{Guid.NewGuid():N}", async _ =>
            {
                running.TrySetResult();
                await release.Task;
            }, subscribed.Token);
            await bus.PublishAsync(topic, new TestMessage("in flight"), cts.Token);
            await running.Task.WaitAsync(cts.Token);

            await _broker.StopAsync(cts.Token);
            await Task.Delay(ClientNoticesTheClose, cts.Token);
            release.TrySetResult();
            await WaitForAsync(entry => entry.EventId == LogEvents.Messaging.MessageNotSettledChannelClosed, cts.Token);

            await subscribed.CancelAsync();
        }
        finally
        {
            release.TrySetResult();
            await bus.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }

        Assert.DoesNotContain(_logs.Entries, entry => entry.EventId == LogEvents.Messaging.MessageProcessingFailed);
        Assert.Contains(_logs.Entries, entry => entry.EventId == LogEvents.Messaging.MessageNotSettledChannelClosed && entry.Level == LogLevel.Information);
    }

    private RabbitMqBus CreateBus()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:rabbitmq"] = _broker.GetConnectionString() })
            .Build();
        return new RabbitMqBus(_loggerFactory.CreateLogger<RabbitMqBus>(), configuration, DevHostEnv, Options.Create(new BusEnvelopeJsonOptions()),
            Options.Create(new MessageRetryOptions()));
    }

    private async Task WaitForAsync(Func<(LogLevel Level, int EventId, string Message), bool> match, CancellationToken cancellationToken)
    {
        while (!_logs.Entries.Any(match))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    private sealed class LogCapture : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, int EventId, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(Entries);

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<(LogLevel Level, int EventId, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((logLevel, eventId.Id, formatter(state, exception) + (exception is null ? string.Empty : $" [{exception.GetType().Name}: {exception.Message}]")));
        }
    }
}
