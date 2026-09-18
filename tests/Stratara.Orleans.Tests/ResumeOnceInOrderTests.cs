using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Diagnostics;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A resumed command runs once and in its scope's order: the dispatch takes the record's time before anything is
/// awaited, strictly increasing per aggregate; the resumer keeps a command at either bound and hands the rest over with
/// the claim's stamp; the receiver's lease takes a stamped hand-over over only from that stamp and drops it otherwise;
/// and a conflict is recorded as a conflict.
/// </summary>
public sealed class ResumeOnceInOrderTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 18, 12, 0, 0, 500, TimeSpan.Zero);
    private static readonly SessionContext Session = new("corr", null, null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null);

    public sealed record Probe(Guid AggregateId, int Sequence) : ICommand, IAggregateScopedCommand;

    public sealed record Unscoped(int Sequence) : ICommand;

    [Fact]
    public async Task Two_dispatches_whose_records_complete_in_reverse_keep_the_order_they_were_dispatched_in()
    {
        var clock = new FakeTimeProvider(Start);
        var firstRecord = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorded = new List<(Guid Intent, DateTimeOffset At)>();
        var intents = new Mock<ICommandIntentStore>();
        intents.Setup(s => s.RecordAsync(It.IsAny<Guid>(), It.IsAny<CommandEnvelope>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns((Guid intent, CommandEnvelope _, Guid? _, bool _, DateTimeOffset at, CancellationToken _) =>
            {
                lock (recorded)
                {
                    recorded.Add((intent, at));
                    return recorded.Count == 1 ? firstRecord.Task : Task.CompletedTask;
                }
            });
        await using var provider = Dispatching(intents.Object, clock);
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
        var aggregate = Guid.NewGuid();

        var first = dispatcher.EnqueueCommandAsync(new Probe(aggregate, 1));
        var second = dispatcher.EnqueueCommandAsync(new Probe(aggregate, 2));
        Assert.Equal(2, recorded.Count);
        firstRecord.SetResult();
        var firstId = await first;
        var secondId = await second;

        var firstAt = recorded.Single(r => r.Intent == firstId).At;
        var secondAt = recorded.Single(r => r.Intent == secondId).At;
        Assert.Equal(Start, firstAt);
        Assert.True(secondAt - firstAt >= TimeSpan.FromMilliseconds(1), $"the second dispatch was recorded at {secondAt:O}, the first at {firstAt:O}");
    }

    [Fact]
    public async Task A_dispatch_takes_its_time_from_the_registered_clock_and_only_an_aggregates_order_is_stepped()
    {
        var clock = new FakeTimeProvider(Start);
        var recorded = new List<DateTimeOffset>();
        var intents = new Mock<ICommandIntentStore>();
        intents.Setup(s => s.RecordAsync(It.IsAny<Guid>(), It.IsAny<CommandEnvelope>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, CommandEnvelope _, Guid? _, bool _, DateTimeOffset at, CancellationToken _) => recorded.Add(at))
            .Returns(Task.CompletedTask);
        await using var provider = Dispatching(intents.Object, clock);
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();

        await dispatcher.EnqueueCommandAsync(new Unscoped(1));
        await dispatcher.EnqueueCommandAsync(new Unscoped(2));
        clock.Advance(TimeSpan.FromSeconds(1));
        var aggregate = Guid.NewGuid();
        await dispatcher.EnqueueCommandAsync(new Probe(aggregate, 1));
        await dispatcher.EnqueueCommandAsync(new Probe(Guid.NewGuid(), 1));
        await dispatcher.EnqueueCommandAsync(new Probe(aggregate, 2));

        var later = Start.AddSeconds(1);
        Assert.Equal([Start, Start, later, later, later.AddMilliseconds(1)], recorded);
    }

    [Theory]
    [InlineData(0, 0, 3, false)]
    [InlineData(2, 0, 3, false)]
    [InlineData(3, 0, 3, true)]
    [InlineData(0, 5, 3, false)]
    [InlineData(0, 6, 3, true)]
    [InlineData(0, 0, 1, false)]
    [InlineData(1, 0, 1, true)]
    [InlineData(1, 6, 1, true)]
    public void A_command_is_kept_at_the_delivery_bound_or_past_the_conflict_bound(int attempts, int conflicts, int maxDeliveryAttempts, bool kept)
    {
        var resumer = Resumer(new Mock<ICommandIntentStore>().Object, new Grains(), new FakeTimeProvider(Start), new MessageRetryOptions { MaxDeliveryAttempts = maxDeliveryAttempts, MaxConflictRequeues = 5 });
        var intent = Recorded(Guid.NewGuid()) with { AttemptCount = attempts, ConflictCount = conflicts, LastFailure = "failed" };

        Assert.Equal(kept, resumer.IsExhausted(intent));
    }

    [Fact]
    public async Task A_resumption_keeps_a_command_at_its_bound_and_hands_the_rest_over_with_the_claims_stamp()
    {
        var clock = new FakeTimeProvider(Start.AddTicks(1234));
        var exhausted = Recorded(Guid.NewGuid()) with { AttemptCount = 3, LastFailure = "failed" };
        var conflicted = Recorded(Guid.NewGuid()) with { ConflictCount = 6, LastFailure = "conflict" };
        var due = Recorded(Guid.NewGuid()) with { AttemptCount = 2, ConflictCount = 5, LastFailure = "failed" };
        DateTimeOffset? claimedAt = null;
        var intents = new Mock<ICommandIntentStore>();
        intents.Setup(s => s.GetDueAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([exhausted, conflicted, due]);
        intents.Setup(s => s.ClaimAsync(It.IsAny<IReadOnlyList<RecordedIntent>>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<RecordedIntent> claimed, DateTimeOffset at, CancellationToken _) =>
            {
                claimedAt = at;
                return [.. claimed.Select(intent => intent.Id)];
            });
        var grains = new Grains();

        var pass = await Resumer(intents.Object, grains, clock, new MessageRetryOptions { MaxDeliveryAttempts = 3, MaxConflictRequeues = 5 }).ResumeDueAsync(10, CancellationToken.None);

        Assert.Equal(1, pass.Resumed);
        intents.Verify(s => s.KeepAsync(exhausted.Id, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        intents.Verify(s => s.KeepAsync(conflicted.Id, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(Start, claimedAt);
        var handedOver = Assert.Single(grains.Accepted);
        Assert.Equal(due.Id, handedOver.Intent);
        Assert.Equal(Start, handedOver.Envelope.ClaimedAt);
    }

    [Fact]
    public async Task A_stamped_hand_over_whose_stamp_moved_is_dropped_and_logged_without_a_lease()
    {
        var intentId = Guid.NewGuid();
        var logger = new RecordingLogger();
        var intents = new Mock<ICommandIntentStore>();
        intents.Setup(s => s.TryRenewFromAsync(intentId, Start, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var lease = await IntentLease.StartAsync(LeaseServices(intents.Object, logger), intentId, Start);

        Assert.Null(lease);
        var dropped = Assert.Single(logger.Entries, e => e.EventId.Id == LogEvents.Orleans.IntentHandOverDropped);
        Assert.Equal(LogLevel.Debug, dropped.Level);
        Assert.Contains(intentId.ToString(), dropped.Message, StringComparison.Ordinal);
        intents.Verify(s => s.RenewAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_stamped_hand_over_that_takes_the_stamp_over_is_leased_without_a_second_renewal()
    {
        var intentId = Guid.NewGuid();
        var intents = new Mock<ICommandIntentStore>();
        intents.Setup(s => s.TryRenewFromAsync(intentId, Start, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await using var lease = await IntentLease.StartAsync(LeaseServices(intents.Object, new RecordingLogger()), intentId, Start);

        Assert.NotNull(lease);
        intents.Verify(s => s.RenewAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_fencing_renewal_the_store_fails_fails_the_hand_over_instead_of_dropping_it()
    {
        var intentId = Guid.NewGuid();
        var logger = new RecordingLogger();
        var intents = new Mock<ICommandIntentStore>();
        intents.Setup(s => s.TryRenewFromAsync(intentId, Start, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException("the store did not answer"));

        await Assert.ThrowsAsync<TimeoutException>(() => IntentLease.StartAsync(LeaseServices(intents.Object, logger), intentId, Start));
        Assert.DoesNotContain(logger.Entries, e => e.EventId.Id == LogEvents.Orleans.IntentHandOverDropped);
    }

    [Fact]
    public async Task A_late_unstamped_hand_over_whose_command_is_gone_or_kept_is_dropped()
    {
        var intentId = Guid.CreateVersion7(Start.AddHours(-1));
        var logger = new RecordingLogger();
        var intents = new Mock<ICommandIntentStore>();
        intents.Setup(s => s.TryRenewAsync(intentId, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var lease = await IntentLease.StartAsync(LeaseServices(intents.Object, logger), intentId, claimedAt: null);

        Assert.Null(lease);
        Assert.Single(logger.Entries, e => e.EventId.Id == LogEvents.Orleans.IntentHandOverDropped);
    }

    [Fact]
    public async Task A_late_unstamped_hand_over_runs_when_its_command_is_recorded_or_its_renewal_fails()
    {
        var recorded = Guid.CreateVersion7(Start.AddHours(-1));
        var unanswered = Guid.CreateVersion7(Start.AddHours(-1));
        var fresh = Guid.CreateVersion7(Start);
        var logger = new RecordingLogger();
        var intents = new Mock<ICommandIntentStore>();
        intents.Setup(s => s.TryRenewAsync(recorded, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        intents.Setup(s => s.TryRenewAsync(unanswered, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException("the store did not answer"));
        var services = LeaseServices(intents.Object, logger);

        await using var first = await IntentLease.StartAsync(services, recorded, claimedAt: null);
        await using var second = await IntentLease.StartAsync(services, unanswered, claimedAt: null);
        await using var third = await IntentLease.StartAsync(services, fresh, claimedAt: null);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        Assert.Single(logger.Entries, e => e.EventId.Id == LogEvents.Orleans.IntentRenewalFailed);
        intents.Verify(s => s.TryRenewAsync(fresh, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_long_lived_scope_forgets_the_order_of_lanes_whose_last_dispatch_is_a_step_behind()
    {
        var clock = new FakeTimeProvider(Start);
        var intents = new Mock<ICommandIntentStore>();
        await using var provider = Dispatching(intents.Object, clock);
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<OrleansCommandDispatcher>();

        for (var lane = 0; lane < 3; lane++)
        {
            await dispatcher.EnqueueCommandAsync(new Probe(Guid.NewGuid(), 1));
        }

        Assert.Equal(3, dispatcher.RememberedLanes);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await dispatcher.EnqueueCommandAsync(new Probe(Guid.NewGuid(), 1));

        Assert.Equal(1, dispatcher.RememberedLanes);
    }

    [Fact]
    public void A_conflict_is_what_the_bus_transports_count_as_one_and_nothing_else()
    {
        Assert.True(IntentFailure.IsConflict(new ConcurrencyException(Guid.NewGuid(), "Order")));
        Assert.False(IntentFailure.IsConflict(new ConcurrencyConflictException()));
        Assert.False(IntentFailure.IsConflict(new DbUpdateConcurrencyException("the row moved")));
        Assert.False(IntentFailure.IsConflict(new InvalidOperationException("the handler failed")));
    }

    [Fact]
    public async Task A_conflict_is_recorded_as_a_conflict_and_any_other_failure_as_a_failure()
    {
        var intentId = Guid.CreateVersion7(Start);
        var intents = new Mock<ICommandIntentStore>();
        await using var lease = await IntentLease.StartAsync(LeaseServices(intents.Object, new RecordingLogger()), intentId, claimedAt: null)
                                ?? throw new InvalidOperationException("the lease was not started");

        await lease.RecordFailureAsync(new ConcurrencyException(Guid.NewGuid(), "Order"), "Probe", aggregateId: null);
        await lease.RecordFailureAsync(new InvalidOperationException("the handler failed"), "Probe", aggregateId: null);
        await lease.ReturnAttemptAsync();

        intents.Verify(s => s.RecordConflictAsync(intentId, It.Is<string>(f => f.Contains(nameof(ConcurrencyException))), It.IsAny<CancellationToken>()), Times.Once);
        intents.Verify(s => s.RecordFailureAsync(intentId, It.Is<string>(f => f.Contains("the handler failed")), It.IsAny<CancellationToken>()), Times.Once);
        intents.Verify(s => s.ReturnAttemptAsync(intentId, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static RecordedIntent Recorded(Guid aggregateId)
    {
        var intentId = Guid.NewGuid();
        return new RecordedIntent(intentId, new CommandEnvelope(intentId, "{}", "Probe", "{}"), aggregateId, Heavy: false, AttemptCount: 0, LastHandedOverAt: null, LastFailure: null);
    }

    private static ServiceProvider LeaseServices(ICommandIntentStore intents, RecordingLogger logger) =>
        new ServiceCollection()
            .AddSingleton(intents)
            .AddSingleton<TimeProvider>(new FakeTimeProvider(Start))
            .AddSingleton(Options.Create(new OrleansDispatchOptions { IntentGrace = TimeSpan.FromMinutes(1) }))
            .AddSingleton<ILogger<IntentLease>>(new TypedLogger<IntentLease>(logger))
            .BuildServiceProvider();

    private static IntentResumer Resumer(ICommandIntentStore intents, Grains grains, TimeProvider clock, MessageRetryOptions retry) => new(
        intents,
        new IntentHandOver(grains.Factory, Options.Create(new HeavyWorkOptions())),
        new AggregateSendLane(),
        Options.Create(new OrleansDispatchOptions { IntentGrace = TimeSpan.Zero }),
        Options.Create(retry),
        clock,
        new TypedLogger<IntentResumer>(new RecordingLogger()));

    /// <summary>The real dispatcher registrations over a mocked store, while a replay holds every hand-over back.</summary>
    private static ServiceProvider Dispatching(ICommandIntentStore intents, TimeProvider clock)
    {
        var serializer = new Mock<ISecureJsonSerializer>();
        serializer.Setup(s => s.SerializeAsync(It.IsAny<It.IsAnyType>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync("{}");
        var sessions = new Mock<ISessionContextProvider>();
        sessions.Setup(s => s.Current).Returns(Session);
        var replay = new Mock<IProjectionReplayState>();
        replay.Setup(r => r.IsReplayActive).Returns(true);
        return new ServiceCollection()
            .AddLogging()
            .AddStrataraOrleansCommandDispatcher()
            .AddScoped(_ => intents)
            .AddSingleton(clock)
            .AddSingleton(new Mock<IGrainFactory>().Object)
            .AddSingleton(serializer.Object)
            .AddSingleton(sessions.Object)
            .AddSingleton(replay.Object)
            .BuildServiceProvider();
    }

    /// <summary>An aggregate grain that records every intent handed to it with its envelope.</summary>
    private sealed class Grains : IAggregateGrain
    {
        private readonly List<(Guid Intent, AggregateCommandEnvelope Envelope)> _accepted = [];

        public Grains()
        {
            var factory = new Mock<IGrainFactory>();
            factory.Setup(f => f.GetGrain<IAggregateGrain>(It.IsAny<Guid>(), null)).Returns(this);
            Factory = factory.Object;
        }

        public IGrainFactory Factory { get; }

        public List<(Guid Intent, AggregateCommandEnvelope Envelope)> Accepted { get { lock (_accepted) { return [.. _accepted]; } } }

        public Task ExecuteAsync(AggregateCommandEnvelope envelope) => Task.CompletedTask;

        public Task AcceptIntentAsync(Guid intentId, AggregateCommandEnvelope envelope)
        {
            lock (_accepted)
            {
                _accepted.Add((intentId, envelope));
            }

            return Task.CompletedTask;
        }

        public Task RunAcceptedAsync() => Task.CompletedTask;
    }
}
