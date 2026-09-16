using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Contracts.Messages;
using Stratara.Diagnostics;
using Stratara.Orleans.Aggregates;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The outbox drain resumes recorded commands wherever an intent store is registered, whichever host dispatched
/// them, and a heavy hand-over holds back nothing that follows it; a drain without an intent store that finds
/// recorded commands says so.
/// </summary>
public sealed class RecordedCommandDrainTests
{
    private static readonly CommandEnvelope Envelope = new(Guid.NewGuid(), "{}", "Probe", "{}");

    [Fact]
    public async Task A_drain_with_an_intent_store_and_no_dispatcher_resumes_the_due_commands()
    {
        var aggregateId = Guid.NewGuid();
        var due = new RecordedIntent(Guid.NewGuid(), Envelope, aggregateId, Heavy: false, AttemptCount: 0, LastHandedOverAt: null, LastFailure: null);
        var grains = new Grains();
        var bus = new Mock<ICommandOutboxDispatcher>(MockBehavior.Strict);

        await using var provider = Drain(grains, IntentsReturning(due), bus.Object).BuildServiceProvider();
        await provider.GetRequiredService<OutboxDrainWork>().RunAsync(CancellationToken.None);

        Assert.Equal([due.Id], grains.Accepted);
    }

    [Fact]
    public async Task Two_due_heavy_commands_on_one_aggregate_hold_back_nothing_after_them()
    {
        var aggregateId = Guid.NewGuid();
        var firstHeavy = new RecordedIntent(Guid.NewGuid(), Envelope, aggregateId, Heavy: true, 0, null, null);
        var secondHeavy = new RecordedIntent(Guid.NewGuid(), Envelope, aggregateId, Heavy: true, 0, null, null);
        var sameAggregate = new RecordedIntent(Guid.NewGuid(), Envelope, aggregateId, Heavy: false, 0, null, null);
        var grains = new Grains();

        await using var provider = Drain(grains, IntentsReturning(firstHeavy, secondHeavy, sameAggregate), bus: null).BuildServiceProvider();
        var run = provider.GetRequiredService<OutboxDrainWork>().RunAsync(CancellationToken.None);

        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))));
        await run;
        Assert.Equal([firstHeavy.Id, secondHeavy.Id], grains.HeavyStarted);
        Assert.Equal([sameAggregate.Id], grains.Accepted);
    }

    [Fact]
    public void A_heavy_hand_over_is_ordered_under_its_intent_and_any_other_under_its_aggregate()
    {
        var intent = Guid.NewGuid();
        var aggregate = Guid.NewGuid();

        Assert.Equal(intent, AggregateSendLane.KeyOf(intent, aggregate, heavy: true));
        Assert.Equal(aggregate, AggregateSendLane.KeyOf(intent, aggregate, heavy: false));
        Assert.Equal(intent, AggregateSendLane.KeyOf(intent, null, heavy: false));
    }

    [Fact]
    public async Task A_drain_without_an_intent_store_that_finds_recorded_commands_warns()
    {
        var logs = new RecordingLoggerProvider();
        var repository = new Mock<IOutboxRepository>();
        repository.Setup(r => r.GetManyAsync<RecordedIntent>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OutboxEntry { Id = Guid.NewGuid(), BucketId = 0, DataJson = "{}", DataTypeName = "recorded", Timestamp = DateTimeOffset.UtcNow }]);
        repository.Setup(r => r.GetManyAsync<CommandEnvelope>(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        repository.Setup(r => r.GetManyAsync<EventBundle>(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await using var provider = new ServiceCollection()
            .AddLogging(builder => builder.AddProvider(logs))
            .AddScoped(_ => UnitOfWork(repository.Object))
            .AddScoped(_ => new Mock<ICommandOutboxDispatcher>().Object)
            .AddScoped(_ => new Mock<IEventBundleOutboxDispatcher>().Object)
            .AddSingleton<OutboxDrainWork>()
            .AddOptions()
            .BuildServiceProvider();

        await provider.GetRequiredService<OutboxDrainWork>().RunAsync(CancellationToken.None);

        var warning = Assert.Single(logs.Events, e => e.Id == LogEvents.Orleans.RecordedCommandsWithoutIntentStore);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("AddStrataraIntentStore", warning.Message, StringComparison.Ordinal);
    }

    private static ServiceCollection Drain(Grains grains, ICommandIntentStore intents, ICommandOutboxDispatcher? bus)
    {
        var repository = new Mock<IOutboxRepository>();
        repository.Setup(r => r.GetManyAsync<EventBundle>(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var services = new ServiceCollection();
        services
            .AddLogging()
            .AddOptions()
            .AddSingleton(grains.Factory)
            .AddScoped(_ => intents)
            .AddScoped(_ => UnitOfWork(repository.Object))
            .AddScoped(_ => new Mock<IEventBundleOutboxDispatcher>().Object)
            .AddSingleton<OutboxDrainWork>()
            .Configure<OrleansDispatchOptions>(options => options.IntentGrace = TimeSpan.Zero);
        if (bus is not null)
        {
            services.AddScoped(_ => bus);
        }

        return services;
    }

    private static ICommandIntentStore IntentsReturning(params RecordedIntent[] due)
    {
        var intents = new Mock<ICommandIntentStore>();
        intents.Setup(s => s.GetDueAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(due);
        intents.Setup(s => s.TryClaimAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return intents.Object;
    }

    private static IWriteUnitOfWork UnitOfWork(IOutboxRepository repository)
    {
        var unitOfWork = new Mock<IWriteUnitOfWork>();
        unitOfWork.Setup(u => u.StartAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new Mock<ITransaction>().Object);
        unitOfWork.Setup(u => u.CreateOutboxRepository(It.IsAny<ITransaction>())).Returns(repository);
        return unitOfWork.Object;
    }

    /// <summary>Grains that record what was handed to them; a heavy unit never finishes.</summary>
    private sealed class Grains
    {
        private readonly RecordingAggregate _aggregate = new();
        private readonly EndlessHeavyWork _heavy = new();

        public Grains()
        {
            var factory = new Mock<IGrainFactory>();
            factory.Setup(f => f.GetGrain<IAggregateGrain>(It.IsAny<Guid>(), null)).Returns(_aggregate);
            factory.Setup(f => f.GetGrain<IHeavyWorkGrain>(It.IsAny<long>(), null)).Returns(_heavy);
            Factory = factory.Object;
        }

        public IGrainFactory Factory { get; }

        public List<Guid> Accepted => _aggregate.Accepted;

        public List<Guid> HeavyStarted => _heavy.Started;
    }

    private sealed class RecordingAggregate : IAggregateGrain
    {
        private readonly List<Guid> _accepted = [];

        public List<Guid> Accepted { get { lock (_accepted) { return [.. _accepted]; } } }

        public Task ExecuteAsync(AggregateCommandEnvelope envelope) => Task.CompletedTask;

        public Task AcceptIntentAsync(Guid intentId, AggregateCommandEnvelope envelope)
        {
            lock (_accepted)
            {
                _accepted.Add(intentId);
            }

            return Task.CompletedTask;
        }

        public Task RunAcceptedAsync() => Task.CompletedTask;
    }

    private sealed class EndlessHeavyWork : IHeavyWorkGrain
    {
        private readonly List<Guid> _started = [];

        public List<Guid> Started { get { lock (_started) { return [.. _started]; } } }

        public Task ExecuteIntentAsync(Guid intentId, AggregateCommandEnvelope envelope)
        {
            lock (_started)
            {
                _started.Add(intentId);
            }

            return new TaskCompletionSource().Task;
        }
    }

    private sealed record LogEvent(int Id, LogLevel Level, string Message);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEvent> _events = [];

        public List<LogEvent> Events { get { lock (_events) { return [.. _events]; } } }

        public ILogger CreateLogger(string categoryName) => new Recorder(this);

        public void Dispose()
        {
        }

        private sealed class Recorder(RecordingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (provider._events)
                {
                    provider._events.Add(new LogEvent(eventId.Id, logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
