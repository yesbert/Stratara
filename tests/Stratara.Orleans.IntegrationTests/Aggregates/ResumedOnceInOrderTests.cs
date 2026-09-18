using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Messages;
using Stratara.Diagnostics;
using Stratara.Orleans.Aggregates;
using Stratara.Orleans.EntityFrameworkCore.Intents;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// A resumed command runs once and in its scope's order, within the bus's bounds, on the PostgreSQL store: two
/// hand-overs of one claim run the handler once, a hand-over after completion is dropped, a failing handler runs as
/// often as the delivery bound says and a conflicting one until the conflict bound, two commands recorded out of order
/// are resumed in the order they were dispatched, and a host's own clock decides when a command is due (scenarios
/// <em>Two drains resume the same command</em>, <em>A hand-over arrives after the command completed</em>,
/// <em>A handler keeps failing</em>, <em>A resumed command meets a concurrency conflict on every attempt</em>,
/// <em>Two commands to one aggregate are recorded out of order</em>, <em>A host registers its own clock</em>).
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ResumedOnceInOrderTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeptTimeout = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(true, "poc_resume_once_aggregate", 11360, 30250)]
    [InlineData(false, "poc_resume_once_runner", 11361, 30251)]
    public async Task Two_hand_overs_of_one_claim_run_the_handler_once(bool namesAnAggregate, string database, int siloPort, int gatewayPort)
    {
        var probes = new RecordedIntentProbes();
        var store = postgres.ConnectionStringFor(database);
        using var host = await RecordedIntentHost.StartAsync(Settings(store, siloPort, gatewayPort, probes, NoDrain));
        var probe = Guid.NewGuid();
        var aggregate = namesAnAggregate ? Guid.NewGuid() : (Guid?)null;
        var intent = await RecordedIntentHost.RecordAsync(host, Guid.NewGuid(), probe, aggregate);

        var (payload, claimedAt) = await ClaimAsync(host, intent);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var handOver = scope.ServiceProvider.GetRequiredService<IntentHandOver>();
            await Task.WhenAll(
                handOver.HandOverAsync(intent, payload, heavy: false, aggregate),
                handOver.HandOverAsync(intent, payload, heavy: false, aggregate));
        }

        Assert.True(await RecordedIntentHost.WaitUntilAsync(() => Task.FromResult(probes.Ran.ContainsKey(probe)), RunTimeout), "the claimed command did not run");
        Assert.True(await GoneAsync(store, intent), "the command's record was not removed");
        await Task.Delay(Settle);
        Assert.Equal(1, probes.RunsOf(probe));
        if (!namesAnAggregate)
        {
            // The runner of a command that names no aggregate holds nothing: the second hand-over waited behind the
            // first and found the stamp moved, which only the fence refuses.
            Assert.Contains(probes.Logs.Entries, e => e.EventId == LogEvents.Orleans.IntentHandOverDropped && e.Message.Contains(intent.ToString(), StringComparison.Ordinal));
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"claimed at {claimedAt:O}");
        await host.StopAsync();
    }

    [Theory]
    [InlineData("aggregate", "poc_resume_after_completion_aggregate", 11362, 30252)]
    [InlineData("runner", "poc_resume_after_completion_runner", 11363, 30253)]
    [InlineData("heavy", "poc_resume_after_completion_heavy", 11364, 30254)]
    public async Task A_hand_over_that_arrives_after_the_command_completed_is_dropped(string path, string database, int siloPort, int gatewayPort)
    {
        var probes = new RecordedIntentProbes();
        var store = postgres.ConnectionStringFor(database);
        using var host = await RecordedIntentHost.StartAsync(Settings(store, siloPort, gatewayPort, probes, services =>
        {
            NoDrain(services);
            services.ConfigureStrataraHeavyWork(options => options.PermitLease = TimeSpan.FromSeconds(2));
        }));
        var probe = Guid.NewGuid();
        var heavy = path == "heavy";
        var aggregate = path == "runner" ? (Guid?)null : Guid.NewGuid();
        var intent = await RecordedIntentHost.RecordAsync(host, Guid.NewGuid(), probe, aggregate, heavy);
        var (payload, _) = await ClaimAsync(host, intent);

        await HandOverAsync(host, intent, payload, heavy, aggregate);
        Assert.True(await RecordedIntentHost.WaitUntilAsync(() => Task.FromResult(probes.Ran.ContainsKey(probe)), RunTimeout), "the claimed command did not run");
        Assert.True(await GoneAsync(store, intent), "the command's record was not removed");

        await HandOverAsync(host, intent, payload, heavy, aggregate);
        await Task.Delay(Settle);

        Assert.Equal(1, probes.RunsOf(probe));
        Assert.Contains(probes.Logs.Entries, e => e.EventId == LogEvents.Orleans.IntentHandOverDropped && e.Message.Contains(intent.ToString(), StringComparison.Ordinal));
        await host.StopAsync();
    }

    [Fact]
    public async Task A_failing_handler_runs_as_often_as_the_delivery_bound_and_a_conflicting_one_until_the_conflict_bound()
    {
        const int maxDeliveryAttempts = 3;
        const int maxConflictRequeues = 3;
        var probes = new RecordedIntentProbes();
        var store = postgres.ConnectionStringFor("poc_resume_bounds");
        using var host = await RecordedIntentHost.StartAsync(Settings(store, 11365, 30255, probes, services => services.Configure<MessageRetryOptions>(options =>
        {
            options.MaxDeliveryAttempts = maxDeliveryAttempts;
            options.MaxConflictRequeues = maxConflictRequeues;
        })));
        var failing = Guid.NewGuid();
        var conflicting = Guid.NewGuid();

        Guid failingIntent;
        Guid conflictingIntent;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
            failingIntent = await dispatcher.EnqueueCommandAsync(new FailingProbe(Guid.NewGuid(), failing));
            conflictingIntent = await dispatcher.EnqueueCommandAsync(new ConflictingProbe(Guid.NewGuid(), conflicting));
        }

        Assert.True(await KeptAsync(store, failingIntent), $"the failing command was not kept; it ran {probes.RunsOf(failing)} times");
        Assert.True(await KeptAsync(store, conflictingIntent), $"the conflicting command was not kept; it ran {probes.RunsOf(conflicting)} times");
        await Task.Delay(Settle);

        Assert.Equal(maxDeliveryAttempts, probes.RunsOf(failing));
        Assert.Equal((maxDeliveryAttempts, 0), await CountsAsync(store, failingIntent));
        Assert.Equal(maxConflictRequeues + 1, probes.RunsOf(conflicting));
        Assert.Equal((0, maxConflictRequeues + 1), await CountsAsync(store, conflictingIntent));
        Assert.Contains("ConcurrencyException", await RecordedIntentHost.ScalarAsync<string>(store, "SELECT last_failure FROM outbox_entry WHERE id = @id", ("id", conflictingIntent)), StringComparison.Ordinal);
        Assert.Contains(probes.Logs.Entries, e => e.EventId == LogEvents.Orleans.CommandKept && e.Message.Contains(failingIntent.ToString(), StringComparison.Ordinal) && e.Message.Contains($"after {maxDeliveryAttempts} attempts", StringComparison.Ordinal));
        await host.StopAsync();
    }

    [Fact]
    public async Task Two_commands_whose_first_record_is_slowed_are_resumed_in_the_order_they_were_dispatched()
    {
        var probes = new RecordedIntentProbes();
        var replay = new SwitchedReplay { IsReplayActive = true };
        var slow = new SlowFirstRecord();
        var store = postgres.ConnectionStringFor("poc_resume_order");
        using var host = await RecordedIntentHost.StartAsync(Settings(store, 11366, 30256, probes, services => services
            .AddSingleton<IProjectionReplayState>(replay)
            .AddSingleton(slow)
            .AddScoped<ICommandIntentStore>(sp => new SlowRecordStore(ActivatorUtilities.CreateInstance<CommandIntentStore<PocWriteDbContext>>(sp), sp.GetRequiredService<SlowFirstRecord>()))));
        var aggregate = Guid.NewGuid();

        // Under an active replay nothing is handed over, which leaves the records as a host that died before either
        // hand-over leaves them; the replay's end lets the drain resume them.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
            var first = dispatcher.EnqueueCommandAsync(new OrderedProbe(aggregate, 1, Slow: true));
            var second = dispatcher.EnqueueCommandAsync(new OrderedProbe(aggregate, 2));
            await Task.WhenAll(first, second);
        }

        Assert.True(slow.Slowed, "the first record was not slowed");
        replay.IsReplayActive = false;

        Assert.True(
            await RecordedIntentHost.WaitUntilAsync(() => Task.FromResult(probes.Order.TryGetValue(aggregate, out var ran) && ran.Count == 2), RunTimeout),
            "the two commands were not resumed");
        Assert.Equal([1, 2], probes.Order[aggregate]);
        await host.StopAsync();
    }

    /// <summary>
    /// The runtime itself cannot run on a clock other than the wall clock, so the host here is the dispatcher and the
    /// resumption over the PostgreSQL store, with the grains replaced by one that records what it is handed.
    /// </summary>
    [Fact]
    public async Task A_host_that_registers_its_own_clock_resumes_a_command_once_that_clock_passes_the_grace()
    {
        var grace = TimeSpan.FromSeconds(30);
        var clock = new SettableClock(new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero));
        var aggregate = new RecordingAggregate();
        var connection = postgres.ConnectionStringFor("poc_resume_clock");
        await using var store = await PocStore<PocWriteDbContext>.CreateAsync(connection);
        await using (var context = await store.CreateContextAsync())
        {
            await context.Database.ExecuteSqlRawAsync("DELETE FROM outbox_entry");
        }

        await using var services = OwnClockHost(store, clock, aggregate, grace);
        Guid intent;
        await using (var scope = services.CreateAsyncScope())
        {
            intent = await scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>().EnqueueCommandAsync(new OrderedProbe(Guid.NewGuid(), 1));
        }

        Assert.Equal([(intent, (DateTimeOffset?)null)], aggregate.HandedOver);
        Assert.Equal(clock.GetUtcNow(), await RecordedIntentHost.ScalarAsync<DateTime>(connection, "SELECT \"timestamp\" FROM outbox_entry WHERE id = @id", ("id", intent)));

        clock.Advance(grace - TimeSpan.FromSeconds(1));
        Assert.Equal(0, (await ResumeAsync(services)).Resumed);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, (await ResumeAsync(services)).Resumed);
        Assert.Equal(intent, aggregate.HandedOver[^1].Intent);
        Assert.NotNull(aggregate.HandedOver[^1].ClaimedAt);
    }

    /// <summary>
    /// The runs a command gets, with the outcomes of its first runs given — <c>s</c> stopped with its silo, <c>c</c> a
    /// conflict, <c>f</c> a failure, <c>-</c> the dispatch's hand-over never made because its host died — and every run
    /// after them failing. The outcomes are recorded through the store as the receiving lease records them; a stop or a
    /// conflict never uses up the delivery bound, and a hand-over that was never made counts as an attempt, as a bus
    /// message's delivery to a consumer that crashed does.
    /// </summary>
    [Theory]
    [InlineData("s", 3, 4)]
    [InlineData("c", 2, 3)]
    [InlineData("c", 3, 4)]
    [InlineData("cs", 2, 4)]
    [InlineData("sc", 3, 5)]
    [InlineData("f", 3, 3)]
    [InlineData("f", 1, 1)]
    [InlineData("s", 1, 2)]
    [InlineData("-", 1, 0)]
    [InlineData("-", 3, 2)]
    public async Task A_command_fails_as_often_as_its_delivery_bound_allows_whatever_came_before(string first, int maxDeliveryAttempts, int runs)
    {
        var counting = await CountingAsync($"poc_resume_counting_{maxDeliveryAttempts}", maxDeliveryAttempts);
        await using var _ = counting;
        var outcomes = new Queue<char>(first);
        var intent = await counting.DispatchAsync();
        var ran = 0;
        if (outcomes.Peek() == '-')
        {
            outcomes.Dequeue();
        }
        else
        {
            ran++;
            await counting.RecordOutcomeAsync(intent, outcomes.TryDequeue(out var outcome) ? outcome : 'f');
        }

        ran += await counting.ResumeUntilKeptAsync(intent, outcomes);

        Assert.Equal(runs, ran);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_kept_command_an_operator_returns_fails_as_often_as_its_delivery_bound_allows_again(int maxDeliveryAttempts)
    {
        var counting = await CountingAsync($"poc_resume_return_{maxDeliveryAttempts}", maxDeliveryAttempts);
        await using var _ = counting;
        var intent = await counting.DispatchAsync();
        await counting.RecordOutcomeAsync(intent, 'f');
        Assert.Equal(maxDeliveryAttempts - 1, await counting.ResumeUntilKeptAsync(intent, new Queue<char>()));

        Assert.Equal(1, await RecordedIntentHost.ScalarAsync<int>(counting.Connection, ReturnKept, ("id", intent)));

        Assert.Equal(maxDeliveryAttempts, await counting.ResumeUntilKeptAsync(intent, new Queue<char>()));
    }

    /// <summary>The operator's return, as the operations guide gives it.</summary>
    private const string ReturnKept =
        "WITH returned AS (UPDATE outbox_entry SET kept_at = NULL, attempt_count = 0, conflict_count = 0, last_failure = NULL " +
        "WHERE id = @id AND kept_at IS NOT NULL RETURNING 1) SELECT count(*)::int FROM returned";

    private async Task<Counting> CountingAsync(string database, int maxDeliveryAttempts)
    {
        var connection = postgres.ConnectionStringFor(database);
        var store = await PocStore<PocWriteDbContext>.CreateAsync(connection);
        await using (var context = await store.CreateContextAsync())
        {
            await context.Database.ExecuteSqlRawAsync("DELETE FROM outbox_entry");
        }

        var clock = new SettableClock(DateTimeOffset.UtcNow);
        var aggregate = new RecordingAggregate();
        return new Counting(store, connection, clock, aggregate, OwnClockHost(store, clock, aggregate, CountingGrace, maxDeliveryAttempts));
    }

    private static readonly TimeSpan CountingGrace = TimeSpan.FromSeconds(30);

    /// <summary>A dispatcher and its resumption over PostgreSQL whose hand-overs are recorded, not run: the test plays the runs.</summary>
    private sealed class Counting(PocStore<PocWriteDbContext> store, string connection, SettableClock clock, RecordingAggregate aggregate, ServiceProvider services) : IAsyncDisposable
    {
        public string Connection => connection;

        public async Task<Guid> DispatchAsync()
        {
            await using var scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>().EnqueueCommandAsync(new OrderedProbe(Guid.NewGuid(), 1));
        }

        /// <summary>What the lease of a run records for <paramref name="outcome"/>.</summary>
        public async Task RecordOutcomeAsync(Guid intent, char outcome)
        {
            await using var scope = services.CreateAsyncScope();
            var intents = scope.ServiceProvider.GetRequiredService<ICommandIntentStore>();
            await (outcome switch
            {
                's' => intents.ReturnAttemptAsync(intent, CancellationToken.None),
                'c' => intents.RecordConflictAsync(intent, "Stratara.Abstractions.EventSourcing.ConcurrencyException: conflict", CancellationToken.None),
                _ => intents.RecordFailureAsync(intent, "System.InvalidOperationException: failed", CancellationToken.None),
            });
        }

        /// <summary>Lets the grace pass and resumes until the command is kept, playing each resumed run; returns the runs.</summary>
        public async Task<int> ResumeUntilKeptAsync(Guid intent, Queue<char> outcomes)
        {
            var runs = 0;
            for (var pass = 0; pass < 20; pass++)
            {
                if (await RecordedIntentHost.ScalarAsync<bool>(connection, "SELECT kept_at IS NOT NULL FROM outbox_entry WHERE id = @id", ("id", intent)))
                {
                    return runs;
                }

                clock.Advance(CountingGrace + TimeSpan.FromSeconds(1));
                var handedOver = aggregate.HandedOver.Count;
                await ResumeAsync(services);
                if (aggregate.HandedOver.Count > handedOver)
                {
                    runs++;
                    await RecordOutcomeAsync(intent, outcomes.TryDequeue(out var outcome) ? outcome : 'f');
                }
            }

            throw new InvalidOperationException($"the command was not kept after {runs} runs");
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_hand_over_the_fence_dropped_does_not_hold_its_command_against_the_next_one()
    {
        var probes = new RecordedIntentProbes();
        var store = postgres.ConnectionStringFor("poc_resume_dropped_release");
        using var host = await RecordedIntentHost.StartAsync(Settings(store, 11369, 30259, probes, NoDrain));
        var aggregate = Guid.NewGuid();
        var blocking = Guid.NewGuid();
        var behind = Guid.NewGuid();
        var gate = probes.Hold(blocking);
        var blockingIntent = await RecordedIntentHost.RecordAsync(host, Guid.NewGuid(), blocking, aggregate);
        var (blockingPayload, _) = await ClaimAsync(host, blockingIntent);
        await HandOverAsync(host, blockingIntent, blockingPayload, heavy: false, aggregate);
        Assert.True(await RecordedIntentHost.WaitUntilAsync(() => Task.FromResult(probes.Started.ContainsKey(blocking)), RunTimeout), "the blocking command did not start");

        var behindIntent = await RecordedIntentHost.RecordAsync(host, Guid.NewGuid(), behind, aggregate);
        var (payload, claimedAt) = await ClaimAsync(host, behindIntent);
        await HandOverAsync(host, behindIntent, payload with { ClaimedAt = claimedAt.AddSeconds(-1) }, heavy: false, aggregate);
        await HandOverAsync(host, behindIntent, payload, heavy: false, aggregate);
        gate.SetResult();

        Assert.True(await RecordedIntentHost.WaitUntilAsync(() => Task.FromResult(probes.Ran.ContainsKey(behind)), RunTimeout), "the hand-over after the dropped one was refused");
        Assert.Equal(1, probes.RunsOf(behind));
        await host.StopAsync();
    }

    [Fact]
    public async Task A_late_dispatch_hand_over_after_a_resumed_run_completed_is_dropped()
    {
        var probes = new RecordedIntentProbes();
        var store = postgres.ConnectionStringFor("poc_resume_late_dispatch");
        using var host = await RecordedIntentHost.StartAsync(Settings(store, 11370, 30260, probes, NoDrain));
        var aggregate = Guid.NewGuid();
        var probe = Guid.NewGuid();

        // An id an hour old: the dispatch's hand-over arrives long after the record, as one delayed past the grace does.
        var intent = await RecordedIntentHost.RecordAsync(host, Guid.NewGuid(), probe, aggregate, intentId: Guid.CreateVersion7(DateTimeOffset.UtcNow.AddHours(-1)));
        var (payload, _) = await ClaimAsync(host, intent);
        await HandOverAsync(host, intent, payload, heavy: false, aggregate);
        Assert.True(await RecordedIntentHost.WaitUntilAsync(() => Task.FromResult(probes.Ran.ContainsKey(probe)), RunTimeout), "the resumed command did not run");
        Assert.True(await GoneAsync(store, intent), "the command's record was not removed");

        await HandOverAsync(host, intent, payload with { ClaimedAt = null }, heavy: false, aggregate);
        await Task.Delay(Settle);

        Assert.Equal(1, probes.RunsOf(probe));
        Assert.Contains(probes.Logs.Entries, e => e.EventId == LogEvents.Orleans.IntentHandOverDropped && e.Message.Contains(intent.ToString(), StringComparison.Ordinal));
        await host.StopAsync();
    }

    /// <summary>A grace no test outlasts, so the drain never resumes on its own and the test hands over itself.</summary>
    private static void NoDrain(IServiceCollection services) =>
        services.Configure<OrleansDispatchOptions>(options => options.IntentGrace = TimeSpan.FromMinutes(10));

    private RecordedIntentSettings Settings(string store, int siloPort, int gatewayPort, RecordedIntentProbes probes, Action<IServiceCollection> services) => new(
        store, postgres.ConnectionStringFor("poc_orleans"), redis.ConnectionString, rabbit.ConnectionString, siloPort, gatewayPort, probes,
        PollingInterval: TimeSpan.FromSeconds(1), BatchSize: 100, Services: services);

    /// <summary>Claims the recorded command as a resumption does, and returns the hand-over it would issue.</summary>
    private static async Task<(AggregateCommandEnvelope Payload, DateTimeOffset ClaimedAt)> ClaimAsync(IHost host, Guid intentId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var intents = scope.ServiceProvider.GetRequiredService<ICommandIntentStore>();
        var now = DateTimeOffset.UtcNow;
        var claimedAt = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var due = (await intents.GetDueAsync(now.AddDays(1), 10, CancellationToken.None)).Where(intent => intent.Id == intentId).ToList();
        Assert.Equal([intentId], await intents.ClaimAsync(due, claimedAt, CancellationToken.None));
        var envelope = due[0].Envelope;
        return (new AggregateCommandEnvelope(envelope.CommandTypeName, envelope.CommandJson, envelope.SessionContextJson, claimedAt), claimedAt);
    }

    private static async Task HandOverAsync(IHost host, Guid intentId, AggregateCommandEnvelope payload, bool heavy, Guid? aggregateId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IntentHandOver>().HandOverAsync(intentId, payload, heavy, aggregateId);
    }

    private static Task<bool> GoneAsync(string store, Guid intentId) =>
        RecordedIntentHost.WaitUntilAsync(
            async () => await RecordedIntentHost.ScalarAsync<int>(store, "SELECT count(*)::int FROM outbox_entry WHERE id = @id", ("id", intentId)) == 0,
            RunTimeout);

    private static Task<bool> KeptAsync(string store, Guid intentId) =>
        RecordedIntentHost.WaitUntilAsync(
            async () => await RecordedIntentHost.ScalarAsync<bool>(store, "SELECT kept_at IS NOT NULL FROM outbox_entry WHERE id = @id", ("id", intentId)),
            KeptTimeout);

    private static async Task<(int Attempts, int Conflicts)> CountsAsync(string store, Guid intentId) =>
        (await RecordedIntentHost.ScalarAsync<int>(store, "SELECT attempt_count FROM outbox_entry WHERE id = @id", ("id", intentId)),
         await RecordedIntentHost.ScalarAsync<int>(store, "SELECT conflict_count FROM outbox_entry WHERE id = @id", ("id", intentId)));

    /// <summary>A replay state the test switches; nothing else of a replay happens.</summary>
    private sealed class SwitchedReplay : IProjectionReplayState
    {
        private volatile bool _active;

        public bool IsReplayActive
        {
            get => _active;
            set => _active = value;
        }

        public void Activate() => _active = true;

        public void Deactivate() => _active = false;

        public void SetFailed(string errorMessage) => _active = false;

        public Task SubscribeToReplayRequestAsync(Func<Task> onReplayRequested, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void RequestReplay()
        {
        }

        public void SetProgress(long processedEvents, long totalEvents)
        {
        }

        public ReplayProgress GetProgress() => new(_active, 0, 0, 0);
    }

    /// <summary>Slows the record of a probe that asks for it, so it completes after the one dispatched behind it.</summary>
    private sealed class SlowFirstRecord
    {
        private volatile bool _slowed;

        public bool Slowed => _slowed;

        public async Task DelayIfAskedAsync(CommandEnvelope envelope)
        {
            if (envelope.CommandJson.Contains("\"slow\":true", StringComparison.OrdinalIgnoreCase))
            {
                _slowed = true;
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }
    }

    /// <summary>The framework's store with the first armed record slowed; every other member passes through.</summary>
    private sealed class SlowRecordStore(ICommandIntentStore inner, SlowFirstRecord slow) : ICommandIntentStore
    {
        public async Task RecordAsync(Guid intentId, CommandEnvelope envelope, Guid? aggregateId, bool heavy, CancellationToken cancellationToken)
        {
            await slow.DelayIfAskedAsync(envelope);
            await inner.RecordAsync(intentId, envelope, aggregateId, heavy, cancellationToken);
        }

        public async Task RecordAsync(Guid intentId, CommandEnvelope envelope, Guid? aggregateId, bool heavy, DateTimeOffset recordedAt, CancellationToken cancellationToken)
        {
            await slow.DelayIfAskedAsync(envelope);
            await inner.RecordAsync(intentId, envelope, aggregateId, heavy, recordedAt, cancellationToken);
        }

        public Task<IReadOnlyList<RecordedIntent>> GetDueAsync(DateTimeOffset handedOverBefore, int batchSize, CancellationToken cancellationToken) =>
            inner.GetDueAsync(handedOverBefore, batchSize, cancellationToken);

        public Task<bool> TryClaimAsync(Guid intentId, DateTimeOffset? expectedLastHandedOverAt, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.TryClaimAsync(intentId, expectedLastHandedOverAt, now, cancellationToken);

        public Task<IReadOnlyList<Guid>> ClaimAsync(IReadOnlyList<RecordedIntent> due, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.ClaimAsync(due, now, cancellationToken);

        public Task RenewAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken) => inner.RenewAsync(intentId, now, cancellationToken);

        public Task<bool> TryRenewFromAsync(Guid intentId, DateTimeOffset claimedAt, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.TryRenewFromAsync(intentId, claimedAt, now, cancellationToken);

        public Task RecordFailureAsync(Guid intentId, string failure, CancellationToken cancellationToken) => inner.RecordFailureAsync(intentId, failure, cancellationToken);

        public Task RecordConflictAsync(Guid intentId, string failure, CancellationToken cancellationToken) => inner.RecordConflictAsync(intentId, failure, cancellationToken);

        public Task ReturnAttemptAsync(Guid intentId, CancellationToken cancellationToken) => inner.ReturnAttemptAsync(intentId, cancellationToken);

        public Task KeepAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken) => inner.KeepAsync(intentId, now, cancellationToken);
    }

    private static ServiceProvider OwnClockHost(PocStore<PocWriteDbContext> store, TimeProvider clock, RecordingAggregate aggregate, TimeSpan grace, int maxDeliveryAttempts = 3)
    {
        var grains = new Mock<IGrainFactory>();
        grains.Setup(f => f.GetGrain<IAggregateGrain>(It.IsAny<Guid>(), null)).Returns(aggregate);
        var serializer = new Mock<ISecureJsonSerializer>();
        serializer.Setup(s => s.SerializeAsync(It.IsAny<It.IsAnyType>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync("{}");
        var sessions = new Mock<ISessionContextProvider>();
        sessions.Setup(s => s.Current).Returns(PocSessions.New());
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton(store.ContextFactory)
            .AddStrataraOrleansCommandDispatcher(options => options.IntentGrace = grace)
            .AddStrataraIntentStore<PocWriteDbContext>()
            .AddSingleton(clock)
            .AddSingleton(grains.Object)
            .AddSingleton(serializer.Object)
            .AddSingleton(sessions.Object)
            .AddSingleton(new Mock<IProjectionReplayState>().Object)
            .Configure<MessageRetryOptions>(options => options.MaxDeliveryAttempts = maxDeliveryAttempts)
            .BuildServiceProvider();
    }

    private static async Task<ResumePass> ResumeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrleansCommandDispatcher>().ResumeDueAsync(10, CancellationToken.None);
    }

    /// <summary>A clock that stands where the test puts it.</summary>
    private sealed class SettableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>An aggregate grain that records what it is handed and runs nothing.</summary>
    private sealed class RecordingAggregate : IAggregateGrain
    {
        private readonly List<(Guid Intent, DateTimeOffset? ClaimedAt)> _handedOver = [];

        public List<(Guid Intent, DateTimeOffset? ClaimedAt)> HandedOver { get { lock (_handedOver) { return [.. _handedOver]; } } }

        public Task ExecuteAsync(AggregateCommandEnvelope envelope) => Task.CompletedTask;

        public Task AcceptIntentAsync(Guid intentId, AggregateCommandEnvelope envelope)
        {
            lock (_handedOver)
            {
                _handedOver.Add((intentId, envelope.ClaimedAt));
            }

            return Task.CompletedTask;
        }

        public Task RunAcceptedAsync() => Task.CompletedTask;
    }
}
