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
/// particular order. A consumer whose channel is already closed is gone, which is what the stop wants: the stop
/// finishes without reporting a failed cleanup. The class runs a broker of its own because it stops it.
/// </summary>
public sealed class RabbitMqBrokerGoneTests : IAsyncLifetime
{
    private static readonly IHostEnvironment DevHostEnv = new TestHostEnv();

    private readonly RabbitMqContainer _broker = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();

    private sealed class TestHostEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Stratara.Outbox.RabbitMQ.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public sealed record TestMessage(string Payload);

    public async ValueTask InitializeAsync() => await _broker.StartAsync();

    public async ValueTask DisposeAsync() => await _broker.DisposeAsync();

    [Fact]
    public async Task BrokerGoneBeforeTheStop_TheSubscriptionsStopWithoutReportingAFailedCleanup()
    {
        var topic = $"test-topic-{Guid.NewGuid():N}";
        var logs = new LogCapture();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:rabbitmq"] = _broker.GetConnectionString() })
            .Build();
        var bus = new RabbitMqBus(loggerFactory.CreateLogger<RabbitMqBus>(), configuration, DevHostEnv, Options.Create(new BusEnvelopeJsonOptions()),
            Options.Create(new MessageRetryOptions()));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var subscribed = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        // A worker subscription and a client subscription, each on a connection of its own.
        await bus.SubscribeAsync<TestMessage>(topic, $"worker-{Guid.NewGuid():N}", _ => Task.CompletedTask, subscribed.Token);
        await bus.SubscribeAsync<TestMessage>(topic, $"default-{Guid.NewGuid():N}", _ => Task.CompletedTask, subscribed.Token);

        await _broker.StopAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);

        await subscribed.CancelAsync();
        await bus.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), cts.Token);

        Assert.Equal(2, logs.Entries.Count(entry => entry.EventId == LogEvents.Messaging.SubscriptionCleanup));
        Assert.Equal(2, logs.Entries.Count(entry => entry.EventId == LogEvents.Messaging.SubscriptionAlreadyClosed && entry.Level == LogLevel.Debug));
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Warning);
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
