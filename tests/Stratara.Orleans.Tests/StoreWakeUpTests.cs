using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Projections;
using Stratara.Contracts.Messages;
using Stratara.Diagnostics;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Hosting;
using Stratara.Orleans.Projections;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The store wake-ups of a host that hosts no reader of a role: the silo's grain folds the commit's buckets into its own
/// partitions and nudges its own readers; the host wakes every role it does not host, one call per key, without waiting;
/// a role no silo hosts is set aside for the retry period and tried again after it; any other failure is a lost
/// wake-up; and the registration composes with the store-reading roles in either order.
/// </summary>
public sealed class StoreWakeUpTests
{
    private const string NoSagaSilo = "No silo of the cluster registered the sagas role of the Orleans execution model, so its grain cannot be placed.";
    private static readonly TimeSpan Retry = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task The_silo_folds_the_buckets_into_its_own_partitions_and_nudges_only_its_role()
    {
        var mine = new CountingTarget();
        var other = new OtherTarget();
        await using var provider = new ServiceCollection()
            .AddSingleton<INudgeTarget>(mine)
            .AddSingleton<INudgeTarget>(other)
            .BuildServiceProvider();

        await StoreWakeUpGrain<CountingTarget>.WakeLocalAsync(
            StoreWakeUpGrain<CountingTarget>.TargetsOf(provider), new Mock<IGrainFactory>().Object, [3, 7, 19, 35], partitionCount: 16, NullLogger.Instance);

        Assert.Equal([3, 7], mine.Partitions.Order());
        Assert.Equal(0, other.Nudges);
    }

    [Fact]
    public async Task A_nudge_that_cannot_be_sent_is_logged_and_does_not_keep_the_other_partitions_from_theirs()
    {
        var target = new CountingTarget { FailOn = 3 };
        var logger = new RecordingLogger();
        await using var provider = new ServiceCollection().AddSingleton<INudgeTarget>(target).BuildServiceProvider();

        await StoreWakeUpGrain<CountingTarget>.WakeLocalAsync(StoreWakeUpGrain<CountingTarget>.TargetsOf(provider), new Mock<IGrainFactory>().Object, [3, 4], 16, logger);

        Assert.Equal([3, 4], target.Partitions.Order());
        Assert.Single(logger.Entries, entry => entry.EventId.Id == LogEvents.Orleans.NudgeFailed);
    }

    [Fact]
    public void The_host_wakes_every_role_it_does_not_host_once_per_key_and_returns_without_waiting()
    {
        var grains = new Grains { Pending = true };
        var wakeUps = WakeUps(grains, new FakeTimeProvider());
        wakeUps.Wake(grains.Factory, BucketsInTwoKeys());

        Assert.Equal(2, grains.ProjectionCalls);
        Assert.Equal(2, grains.SagaCalls);
    }

    [Fact]
    public void The_event_types_travel_beside_the_call_and_leave_the_callers_context_as_it_was()
    {
        var grains = new Grains();
        var wakeUps = WakeUps(grains, new FakeTimeProvider());
        global::Orleans.Runtime.RequestContext.Set(WakeUpEventTypes.Key, "the caller's own");
        try
        {
            wakeUps.Wake(grains.Factory, [5], ["Created", "Incremented"]);

            Assert.Equal(new[] { "Created", "Incremented" }, grains.ProjectionCarriedTypes);
            Assert.Equal("the caller's own", global::Orleans.Runtime.RequestContext.Get(WakeUpEventTypes.Key));
        }
        finally
        {
            global::Orleans.Runtime.RequestContext.Remove(WakeUpEventTypes.Key);
        }

        wakeUps.Wake(grains.Factory, [5], ["Created"]);
        Assert.Null(global::Orleans.Runtime.RequestContext.Get(WakeUpEventTypes.Key));

        wakeUps.Wake(grains.Factory, [5]);
        Assert.Null(grains.ProjectionCarriedTypes);
    }

    [Fact]
    public void A_silo_takes_the_event_types_out_of_the_context_and_reads_none_where_the_host_sent_none()
    {
        global::Orleans.Runtime.RequestContext.Set(WakeUpEventTypes.Key, new[] { "Created" });

        Assert.Equal(new[] { "Created" }, WakeUpEventTypes.Take());
        Assert.Null(global::Orleans.Runtime.RequestContext.Get(WakeUpEventTypes.Key));
        Assert.Null(WakeUpEventTypes.Take());
    }

    [Fact]
    public void A_role_the_host_hosts_itself_is_not_woken_through_the_cluster()
    {
        var grains = new Grains();
        var services = new ServiceCollection();
        RolePlacement.Publish(services, ExecutionRole.Projections);
        using var provider = services.BuildServiceProvider();
        var wakeUps = new ClusterWakeUps(provider, Options.Create(new StoreWakeUpOptions()), Options.Create(new CommitOrderOptions()), new FakeTimeProvider(), new LoggerOf<ClusterWakeUps>(new RecordingLogger()));

        wakeUps.Wake(grains.Factory, [5]);

        Assert.Equal([ExecutionRole.Sagas], wakeUps.Roles);
        Assert.Equal(0, grains.ProjectionCalls);
        Assert.Equal(1, grains.SagaCalls);
    }

    [Fact]
    public void A_role_no_silo_hosts_is_set_aside_for_the_retry_period_and_tried_again_after_it()
    {
        var grains = new Grains { SagaFailure = new InvalidOperationException(NoSagaSilo) };
        var time = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var wakeUps = WakeUps(grains, time, logger);
        int[] stream = [5];

        wakeUps.Wake(grains.Factory, stream);
        wakeUps.Wake(grains.Factory, stream);
        time.Advance(Retry / 2);
        wakeUps.Wake(grains.Factory, stream);

        Assert.Equal(1, grains.SagaCalls);
        Assert.Equal(3, grains.ProjectionCalls);
        Assert.Single(logger.Entries, entry => entry.EventId.Id == LogEvents.Orleans.StoreWakeUpRoleAbsent);

        grains.SagaFailure = null;
        time.Advance(Retry);
        wakeUps.Wake(grains.Factory, stream);
        wakeUps.Wake(grains.Factory, stream);

        Assert.Equal(3, grains.SagaCalls);
        Assert.Single(logger.Entries, entry => entry.EventId.Id == LogEvents.Orleans.StoreWakeUpRoleBack);
    }

    [Fact]
    public void Any_other_failure_is_a_lost_wake_up_and_does_not_set_the_role_aside()
    {
        var grains = new Grains { SagaFailure = new InvalidOperationException("The grain interface is unknown to this silo.") };
        var logger = new RecordingLogger();
        var wakeUps = WakeUps(grains, new FakeTimeProvider(), logger);
        int[] stream = [5];

        wakeUps.Wake(grains.Factory, stream);
        wakeUps.Wake(grains.Factory, stream);

        Assert.Equal(2, grains.SagaCalls);
        Assert.DoesNotContain(logger.Entries, entry => entry.EventId.Id == LogEvents.Orleans.StoreWakeUpRoleAbsent);
        Assert.Equal(2, logger.Entries.Count(entry => entry.EventId.Id == LogEvents.Orleans.NudgeFailed && entry.Level == LogLevel.Debug));
    }

    [Fact]
    public async Task On_a_client_the_dispatcher_wakes_through_the_cluster_and_stores_nothing()
    {
        var grains = new Grains();
        var services = Base(grains).AddStrataraStoreWakeUps();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IEventBundleOutboxDispatcher>();

        await dispatcher.EnqueueEventBundleAsync(Bundle(), TestContext.Current.CancellationToken);

        Assert.False(dispatcher.StoresBundlesWithCommit);
        Assert.Equal(1, grains.ProjectionCalls);
        Assert.Equal(1, grains.SagaCalls);
    }

    [Fact]
    public void The_registration_and_a_store_reading_role_compose_the_same_in_either_order()
    {
        var before = Base(new Grains()).AddStrataraStoreWakeUps().AddStrataraProjectionGrains();
        var after = Base(new Grains()).AddStrataraProjectionGrains().AddStrataraStoreWakeUps();

        Assert.Equal(Shape(before), Shape(after));
    }

    [Fact]
    public async Task Asking_to_keep_the_bus_keeps_the_dispatcher_registered_before()
    {
        var recording = new HybridBundleDispatcherTests.RecordingDispatcher();
        var services = Base(new Grains()).AddSingleton<IEventBundleOutboxDispatcher>(recording).AddStrataraStoreWakeUps(hybrid: true);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IEventBundleOutboxDispatcher>().EnqueueEventBundleAsync(Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(1, recording.Enqueued);
    }

    [Fact]
    public void Asking_to_keep_a_bus_nobody_registered_fails_naming_both()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Base(new Grains()).AddStrataraStoreWakeUps(hybrid: true));

        Assert.Contains("hybrid", failure.Message, StringComparison.Ordinal);
        Assert.Contains("AddOutboxDispatcher", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_retry_period_that_is_not_positive_fails_naming_the_setting()
    {
        var services = new ServiceCollection().AddStrataraStoreWakeUps(options => options.AbsentRoleRetry = TimeSpan.Zero);
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<StoreWakeUpOptions>>().Value);

        Assert.Contains("AbsentRoleRetry", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_silo_that_registers_no_reader_of_the_role_refuses_the_wake_up()
    {
        await using var provider = new ServiceCollection().AddSingleton<INudgeTarget>(new OtherTarget()).BuildServiceProvider();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => StoreWakeUpGrain<CountingTarget>.WakeLocalAsync(
            StoreWakeUpGrain<CountingTarget>.TargetsOf(provider), new Mock<IGrainFactory>().Object, [3], 16, NullLogger.Instance));

        Assert.Contains(nameof(CountingTarget), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_more_wake_ups_of_a_role_await_their_answer_than_the_bound()
    {
        var grains = new Grains { Pending = true };
        var logger = new RecordingLogger();
        var wakeUps = WakeUps(grains, new FakeTimeProvider(), logger);

        for (var commit = 0; commit < ClusterWakeUps.MaxOutstandingPerRole + 10; commit++)
        {
            wakeUps.Wake(grains.Factory, [5]);
        }

        Assert.Equal(ClusterWakeUps.MaxOutstandingPerRole, grains.ProjectionCalls);
        Assert.Equal(ClusterWakeUps.MaxOutstandingPerRole, grains.SagaCalls);
        Assert.Equal(20, logger.Entries.Count(entry => entry.EventId.Id == LogEvents.Orleans.NudgeFailed));
    }

    [Fact]
    public void An_answer_to_a_wake_up_sent_before_the_role_was_set_aside_neither_ends_the_set_aside_nor_logs_it_again()
    {
        var grains = new Grains();
        var time = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var wakeUps = WakeUps(grains, time, logger);
        var late = new TaskCompletionSource();
        grains.SagaAnswers.Enqueue(late.Task);
        grains.SagaAnswers.Enqueue(Task.FromException(new InvalidOperationException(NoSagaSilo)));

        wakeUps.Wake(grains.Factory, BucketsInTwoKeys());
        late.SetResult();
        wakeUps.Wake(grains.Factory, [5]);

        Assert.Equal(2, grains.SagaCalls);
        Assert.Single(logger.Entries, entry => entry.EventId.Id == LogEvents.Orleans.StoreWakeUpRoleAbsent);
        Assert.DoesNotContain(logger.Entries, entry => entry.EventId.Id == LogEvents.Orleans.StoreWakeUpRoleBack);
    }

    [Fact]
    public void A_wake_up_that_throws_a_cancellation_at_once_does_not_fail_the_commit()
    {
        var grains = new Grains { SagaThrowsAtOnce = new OperationCanceledException("the client is stopping") };
        var wakeUps = WakeUps(grains, new FakeTimeProvider());

        var thrown = Record.Exception(() => wakeUps.Wake(grains.Factory, [5]));

        Assert.Null(thrown);
        Assert.Equal(1, grains.ProjectionCalls);
    }

    private static ClusterWakeUps WakeUps(Grains grains, FakeTimeProvider time, RecordingLogger? logger = null)
    {
        _ = grains;
        using var provider = new ServiceCollection().BuildServiceProvider();
        return new ClusterWakeUps(
            provider,
            Options.Create(new StoreWakeUpOptions { AbsentRoleRetry = Retry }),
            Options.Create(new CommitOrderOptions()),
            time,
            new LoggerOf<ClusterWakeUps>(logger ?? new RecordingLogger()));
    }

    /// <summary>Two buckets that fall on two different keys under the default partition count.</summary>
    private static int[] BucketsInTwoKeys() => [3, 4];

    private static IServiceCollection Base(Grains grains) => new ServiceCollection()
        .AddLogging()
        .AddSingleton(grains.Factory)
        .AddSingleton(new Mock<IProjectionReplayState>().Object)
        .AddSingleton(new Mock<IProjectionHandler>().Object);

    private static EventBundle Bundle() =>
        new([new EventMessage(Guid.NewGuid(), 1, "{}", Guid.NewGuid(), "Created", "Aggregate", Guid.Empty, Guid.Empty, Guid.Empty, null)], "{}");

    private static List<string> Shape(IServiceCollection services) =>
        [.. services.Select(d => $"{d.ServiceType.FullName}|{d.ServiceKey}|{d.Lifetime}|{(d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType)?.FullName}").Order(StringComparer.Ordinal)];

    /// <summary>The wake-up grains of both roles, counting their calls; one that fails throws what it is given.</summary>
    private sealed class Grains
    {
        private readonly ProjectionWakeUp _projection;
        private readonly SagaWakeUp _saga;

        public Grains()
        {
            _projection = new ProjectionWakeUp(this);
            _saga = new SagaWakeUp(this);
            var factory = new Mock<IGrainFactory>();
            factory.Setup(f => f.GetGrain<IProjectionWakeUpGrain>(It.IsAny<long>(), null)).Returns(_projection);
            factory.Setup(f => f.GetGrain<ISagaWakeUpGrain>(It.IsAny<long>(), null)).Returns(_saga);
            Factory = factory.Object;
        }

        public IGrainFactory Factory { get; }

        public bool Pending { get; init; }

        public Exception? SagaFailure { get; set; }

        /// <summary>Answers handed out in order before <see cref="SagaFailure"/> and <see cref="Pending"/> apply.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<Task> SagaAnswers { get; } = new();

        public Exception? SagaThrowsAtOnce { get; init; }

        public int ProjectionCalls => _projection.Calls;

        public object? ProjectionCarriedTypes => _projection.CarriedTypes;

        public int SagaCalls => _saga.Calls;

        private Task Answer(Exception? failure) =>
            failure is not null ? Task.FromException(failure) : Pending ? new TaskCompletionSource().Task : Task.CompletedTask;

        private sealed class ProjectionWakeUp(Grains grains) : IProjectionWakeUpGrain
        {
            private int _calls;

            public int Calls => Volatile.Read(ref _calls);

            public object? CarriedTypes { get; private set; }

            public Task WakeAsync(int[] buckets)
            {
                Interlocked.Increment(ref _calls);
                CarriedTypes = global::Orleans.Runtime.RequestContext.Get(WakeUpEventTypes.Key);
                return grains.Answer(failure: null);
            }
        }

        private sealed class SagaWakeUp(Grains grains) : ISagaWakeUpGrain
        {
            private int _calls;

            public int Calls => Volatile.Read(ref _calls);

            public Task WakeAsync(int[] buckets)
            {
                if (grains.SagaThrowsAtOnce is { } thrown)
                {
                    throw thrown;
                }

                Interlocked.Increment(ref _calls);
                return grains.SagaAnswers.TryDequeue(out var answer) ? answer : grains.Answer(grains.SagaFailure);
            }
        }
    }

    internal class CountingTarget : INudgeTarget
    {
        private readonly List<int> _partitions = [];

        public int? FailOn { get; init; }

        public List<int> Partitions { get { lock (_partitions) { return [.. _partitions]; } } }

        public IReadOnlyList<string> ConsumerNames => ["counting"];

        public Task NudgeAsync(IGrainFactory grainFactory, int partition)
        {
            lock (_partitions)
            {
                _partitions.Add(partition);
            }

            return partition == FailOn ? Task.FromException(new InvalidOperationException("the silo is stopping")) : Task.CompletedTask;
        }

        public Task EnsureRunningAsync(IGrainFactory grainFactory, int partition) => Task.CompletedTask;
    }

    internal sealed class OtherTarget : INudgeTarget
    {
        private int _nudges;

        public int Nudges => _nudges;

        public IReadOnlyList<string> ConsumerNames => ["other"];

        public Task NudgeAsync(IGrainFactory grainFactory, int partition)
        {
            Interlocked.Increment(ref _nudges);
            return Task.CompletedTask;
        }

        public Task EnsureRunningAsync(IGrainFactory grainFactory, int partition) => Task.CompletedTask;
    }

    private sealed class LoggerOf<T>(RecordingLogger recording) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => recording.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => recording.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            recording.Log(logLevel, eventId, state, exception, formatter);
    }
}
