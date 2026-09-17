using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Projections;
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

    [Fact]
    public async Task A_resumption_held_back_by_a_replay_is_logged_once_when_it_begins_and_once_when_it_ends()
    {
        var logs = new RecordingLoggerProvider();
        var active = true;
        var replay = new Mock<IProjectionReplayState>();
        replay.Setup(state => state.IsReplayActive).Returns(() => active);
        var due = new RecordedIntent(Guid.NewGuid(), Envelope, Guid.NewGuid(), Heavy: false, 0, null, null);
        var grains = new Grains();
        var services = Drain(grains, IntentsReturning(due), bus: null, logs);
        services.AddSingleton(replay.Object);
        await using var provider = services.BuildServiceProvider();
        var drain = provider.GetRequiredService<OutboxDrainWork>();

        for (var run = 0; run < 3; run++)
        {
            await drain.RunAsync(CancellationToken.None);
        }

        Assert.Single(logs.Events, e => e.Id == LogEvents.Orleans.ResumeHeldBackByReplay);
        Assert.DoesNotContain(logs.Events, e => e.Id == LogEvents.Orleans.ResumeReleasedAfterReplay);
        Assert.Empty(grains.Accepted);

        active = false;
        await drain.RunAsync(CancellationToken.None);
        await drain.RunAsync(CancellationToken.None);

        var released = Assert.Single(logs.Events, e => e.Id == LogEvents.Orleans.ResumeReleasedAfterReplay);
        Assert.Equal(LogLevel.Information, released.Level);
        Assert.Single(logs.Events, e => e.Id == LogEvents.Orleans.ResumeHeldBackByReplay);
        Assert.Contains(due.Id, grains.Accepted);
    }

    [Fact]
    public async Task A_drain_that_was_never_held_back_logs_no_release()
    {
        var logs = new RecordingLoggerProvider();
        var grains = new Grains();

        await using var provider = Drain(grains, IntentsReturning(), bus: null, logs).BuildServiceProvider();
        await provider.GetRequiredService<OutboxDrainWork>().RunAsync(CancellationToken.None);

        Assert.DoesNotContain(logs.Events, e => e.Id is LogEvents.Orleans.ResumeHeldBackByReplay or LogEvents.Orleans.ResumeReleasedAfterReplay);
    }

    [Fact]
    public async Task A_run_passes_again_while_its_passes_are_full_and_ends_at_the_first_short_one()
    {
        var calls = 0;
        var intents = new Mock<ICommandIntentStore>();
        intents.Setup(s => s.GetDueAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset _, int batchSize, CancellationToken _) => Due(++calls <= 3 ? batchSize : 1));
        ClaimEverything(intents);
        var services = Drain(new Grains(), intents.Object, bus: null);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow))
            .Configure<OutboxDrainOptions>(options => options.BatchSize = 2);
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<OutboxDrainWork>().RunAsync(CancellationToken.None);

        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task A_run_of_full_passes_ends_once_it_has_lasted_its_period()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var calls = 0;
        var intents = new Mock<ICommandIntentStore>();
        intents.Setup(s => s.GetDueAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset _, int batchSize, CancellationToken _) =>
            {
                calls++;
                clock.Advance(TimeSpan.FromSeconds(2));
                return Due(batchSize);
            });
        ClaimEverything(intents);
        var services = Drain(new Grains(), intents.Object, bus: null);
        services.AddSingleton<TimeProvider>(clock)
            .Configure<OutboxDrainOptions>(options =>
            {
                options.BatchSize = 2;
                options.PollingInterval = TimeSpan.FromSeconds(5);
            });
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<OutboxDrainWork>().RunAsync(CancellationToken.None);

        Assert.Equal(3, calls);
    }

    private static IReadOnlyList<RecordedIntent> Due(int count) =>
    [
        .. Enumerable.Range(0, count).Select(_ => new RecordedIntent(Guid.NewGuid(), Envelope, Guid.NewGuid(), Heavy: false, 0, null, null)),
    ];

    private static void ClaimEverything(Mock<ICommandIntentStore> intents) =>
        intents.Setup(s => s.ClaimAsync(It.IsAny<IReadOnlyList<RecordedIntent>>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<RecordedIntent> claimed, DateTimeOffset _, CancellationToken _) => [.. claimed.Select(intent => intent.Id)]);

    private static ServiceCollection Drain(Grains grains, ICommandIntentStore intents, ICommandOutboxDispatcher? bus, RecordingLoggerProvider? logs = null)
    {
        var repository = new Mock<IOutboxRepository>();
        repository.Setup(r => r.GetManyAsync<EventBundle>(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var services = new ServiceCollection();
        services
            .AddLogging(builder =>
            {
                if (logs is not null)
                {
                    builder.AddProvider(logs);
                }
            })
            .AddOptions()
            .AddSingleton<ReplaySuspensionTracker>()
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
        intents.Setup(s => s.ClaimAsync(It.IsAny<IReadOnlyList<RecordedIntent>>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<RecordedIntent> claimed, DateTimeOffset _, CancellationToken _) => [.. claimed.Select(intent => intent.Id)]);
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
    internal sealed class Grains
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

    internal sealed class RecordingAggregate : IAggregateGrain
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

    internal sealed class EndlessHeavyWork : IHeavyWorkGrain
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
