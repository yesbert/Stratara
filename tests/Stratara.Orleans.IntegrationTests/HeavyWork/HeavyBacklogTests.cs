using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Messages;
using Stratara.Orleans.IntegrationTests.Aggregates;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Timers;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.IntegrationTests.HeavyWork;

/// <summary>
/// Scenarios of <em>Waiting costs a recorded command no attempt</em>, on the PostgreSQL store: a burst of heavy
/// commands many times the cluster-wide limit, into a pool that holds far fewer than are dispatched, completes every
/// command once with no record ever above one attempt; the same holds when the silo holding the burst is stopped and
/// another takes over; and the store renews a batch of records in one statement, leaving kept and unlisted ones alone.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class HeavyBacklogTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int Units = 200;
    private const int DelayMs = 300;
    private const int ClusterWideLimit = 2;
    private const int QueuedLimitPerPool = 16;
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(4);

    [Fact]
    public async Task A_burst_many_times_the_limit_completes_every_command_once_and_never_spends_a_second_attempt()
    {
        var store = postgres.ConnectionStringFor("poc_heavy_backlog");
        using var silo = await StartAsync(store, $"stratara-poc-backlog-{Guid.NewGuid():N}", siloPort: 11510, gatewayPort: 30510);
        var units = await DispatchAsync(silo, Units);

        var (drained, mostAttempts) = await WaitForDrainAsync(store);

        Assert.True(drained, $"the burst did not drain: {await ScalarAsync(store, "SELECT count(*) FROM outbox_entry")} records left");
        Assert.True(mostAttempts <= 1, $"a record reached attempt {mostAttempts} although no handler failed");
        Assert.Equal(0, await ScalarAsync(store, "SELECT count(*) FROM outbox_entry WHERE kept_at IS NOT NULL"));
        var executions = silo.Services.GetRequiredService<ExecutionCount>();
        Assert.All(units, unit => Assert.Equal(1, executions.Completed(unit)));
        await silo.StopAsync();
    }

    [Fact]
    public async Task A_silo_stopped_during_a_burst_hands_it_over_without_spending_a_second_attempt()
    {
        var store = postgres.ConnectionStringFor("poc_heavy_backlog_stop");
        var cluster = $"stratara-poc-backlog-stop-{Guid.NewGuid():N}";
        var executions = new ExecutionCount();
        using var first = await StartAsync(store, cluster, siloPort: 11511, gatewayPort: 30511, executions);
        var units = await DispatchAsync(first, Units / 2);
        using var second = await StartAsync(store, cluster, siloPort: 11512, gatewayPort: 30512, executions, joining: true);
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var sampled = SampleMostAttemptsAsync(store, TimeSpan.FromSeconds(20));
        using (var stopTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
        {
            await first.StopAsync(stopTimeout.Token);
        }

        var duringStop = await sampled;
        var (drained, afterStop) = await WaitForDrainAsync(store);

        Assert.True(drained, $"the burst did not drain on the remaining silo: {await ScalarAsync(store, "SELECT count(*) FROM outbox_entry")} records left");
        Assert.True(Math.Max(duringStop, afterStop) <= 1, $"a record reached attempt {Math.Max(duringStop, afterStop)} across the stop");
        Assert.Equal(0, await ScalarAsync(store, "SELECT count(*) FROM outbox_entry WHERE kept_at IS NOT NULL"));
        Assert.All(units, unit => Assert.Equal(1, executions.Completed(unit)));
        await second.StopAsync();
    }

    /// <summary>
    /// The chain the finding observed, reproduced: the store fails every renewal for four graces, as an exhausted pool
    /// does, so the records of waiting commands lapse and the drain hands them over again to the pool that still holds
    /// them. Those hand-overs are dropped as duplicates and give their attempts back, so no record climbs to the
    /// delivery bound, none is kept, and every command runs once when the renewals recover.
    /// </summary>
    [Fact]
    public async Task Records_that_lapse_while_their_commands_wait_are_handed_over_again_without_spending_attempts()
    {
        var store = postgres.ConnectionStringFor("poc_heavy_backlog_lapse");
        var renewals = new RenewalSwitch();
        using var silo = await StartAsync(store, $"stratara-poc-backlog-lapse-{Guid.NewGuid():N}", siloPort: 11513, gatewayPort: 30513, renewals: renewals);
        var units = await DispatchAsync(silo, 12, delayMs: 3_000);

        renewals.Failing = true;
        var lapsed = await SampleMostAttemptsAsync(store, Grace * 4);
        renewals.Failing = false;
        var (drained, after) = await WaitForDrainAsync(store);

        Assert.True(renewals.Refused > 0, "no renewal was failed; the test did not reproduce the lapse");
        Assert.True(drained, $"the commands did not drain: {await ScalarAsync(store, "SELECT count(*) FROM outbox_entry")} records left");
        Assert.True(Math.Max(lapsed, after) <= 1, $"a record reached attempt {Math.Max(lapsed, after)} while its command only waited");
        Assert.Equal(0, await ScalarAsync(store, "SELECT count(*) FROM outbox_entry WHERE kept_at IS NOT NULL"));
        var executions = silo.Services.GetRequiredService<ExecutionCount>();
        Assert.All(units, unit => Assert.Equal(1, executions.Completed(unit)));
        await silo.StopAsync();
    }

    [Fact]
    public async Task The_store_renews_a_batch_in_one_statement_and_leaves_kept_and_unlisted_records_alone()
    {
        var connection = postgres.ConnectionStringFor("poc_heavy_backlog_renew");
        await PostgresTimerHostSchema.EnsureDatabaseAsync(connection);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:defaultdb"] = connection }).Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddStrataraIntentStore<PocWriteDbContext>();
        await using var provider = services.BuildServiceProvider();
        await using (var context = await provider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        await using var scope = provider.CreateAsyncScope();
        var intents = scope.ServiceProvider.GetRequiredService<ICommandIntentStore>();
        var (listed, kept, unlisted) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var recordedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        foreach (var id in new[] { listed, kept, unlisted })
        {
            await intents.RecordAsync(id, new CommandEnvelope(id, "{}", "App.Commands.Probe", "{}"), aggregateId: null, heavy: true, recordedAt, TestContext.Current.CancellationToken);
        }

        await intents.KeepAsync(kept, recordedAt, TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;

        await intents.RenewManyAsync([listed, kept], now, TestContext.Current.CancellationToken);

        Assert.Equal(1, await ScalarAsync(connection, $"SELECT count(*) FROM outbox_entry WHERE id = '{listed}' AND last_handed_over_at >= now() - interval '1 minute'"));
        Assert.Equal(0, await ScalarAsync(connection, $"SELECT count(*) FROM outbox_entry WHERE id = '{kept}' AND last_handed_over_at >= now() - interval '1 minute'"));
        Assert.Equal(0, await ScalarAsync(connection, $"SELECT count(*) FROM outbox_entry WHERE id = '{unlisted}' AND last_handed_over_at >= now() - interval '1 minute'"));
    }

    private static async Task<List<Guid>> DispatchAsync(IHost silo, int count, int delayMs = DelayMs)
    {
        var units = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();
        await using var scope = silo.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
        foreach (var unit in units)
        {
            await dispatcher.EnqueueCommandAsync(new CountedHeavyProbe(unit, delayMs), TestContext.Current.CancellationToken);
        }

        return units;
    }

    /// <summary>Waits until the outbox is empty, sampling the highest attempt count any record shows on the way.</summary>
    private static async Task<(bool Drained, long MostAttempts)> WaitForDrainAsync(string store)
    {
        var most = 0L;
        var deadline = DateTimeOffset.UtcNow + DrainTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            most = Math.Max(most, await ScalarAsync(store, "SELECT coalesce(max(attempt_count), 0) FROM outbox_entry"));
            if (await ScalarAsync(store, "SELECT count(*) FROM outbox_entry") == 0)
            {
                return (true, most);
            }

            await Task.Delay(200);
        }

        return (false, most);
    }

    private static async Task<long> SampleMostAttemptsAsync(string store, TimeSpan duration)
    {
        var most = 0L;
        var deadline = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < deadline)
        {
            most = Math.Max(most, await ScalarAsync(store, "SELECT coalesce(max(attempt_count), 0) FROM outbox_entry"));
            await Task.Delay(200);
        }

        return most;
    }

    private static async Task<long> ScalarAsync(string store, string sql)
    {
        await using var connection = new NpgsqlConnection(store);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<IHost> StartAsync(string store, string clusterId, int siloPort, int gatewayPort, ExecutionCount? executions = null, bool joining = false, RenewalSwitch? renewals = null)
    {
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        var orleans = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleans);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = store,
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleans, redis.ConnectionString, siloPort, gatewayPort, clusterId: clusterId)
            .Configure<global::Orleans.Configuration.GrainCollectionOptions>(options => options.DeactivationTimeout = TimeSpan.FromSeconds(2)));
        builder.AddBackendServices();
        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddSingleton(executions ?? new ExecutionCount())
            .AddScoped<ICommandHandler<CountedHeavyProbe>, CountedHeavyProbeHandler>()
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<CountedHeavyProbe>()
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher(options => options.IntentGrace = Grace)
            .AddStrataraIntentStore<PocWriteDbContext>()
            .ConfigureStrataraHeavyWork(options =>
            {
                options.ClusterWideLimit = ClusterWideLimit;
                options.QueuedLimitPerPool = QueuedLimitPerPool;
                options.PermitLease = TimeSpan.FromSeconds(2);
            })
            .AddStrataraSingletonWork<OutboxDrainWork>(options => options.KeepAlivePeriod = TimeSpan.FromSeconds(5))
            .Configure<OutboxDrainOptions>(options => options.PollingInterval = TimeSpan.FromMilliseconds(500));
        if (renewals is not null)
        {
            var registered = builder.Services.Last(d => d.ServiceType == typeof(ICommandIntentStore));
            builder.Services.Remove(registered);
            builder.Services.AddSingleton(renewals);
            builder.Services.AddScoped<ICommandIntentStore>(sp => new FailingRenewals(
                (ICommandIntentStore)ActivatorUtilities.CreateInstance(sp, registered.ImplementationType!), sp.GetRequiredService<RenewalSwitch>()));
        }

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
            if (!joining)
            {
                await context.Database.ExecuteSqlRawAsync("DELETE FROM outbox_entry");
            }
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    /// <summary>Whether the store fails every renewal now, and how many it failed.</summary>
    public sealed class RenewalSwitch
    {
        private int _refused;

        public volatile bool Failing;

        public int Refused => Volatile.Read(ref _refused);

        public void Refuse()
        {
            Interlocked.Increment(ref _refused);
            throw new InvalidOperationException("The connection pool has been exhausted.");
        }
    }

    /// <summary>The framework's store, with renewals that fail while the switch says so; everything else passes through.</summary>
    private sealed class FailingRenewals(ICommandIntentStore inner, RenewalSwitch renewals) : ICommandIntentStore
    {
        public Task RecordAsync(Guid intentId, CommandEnvelope envelope, Guid? aggregateId, bool heavy, CancellationToken cancellationToken) =>
            inner.RecordAsync(intentId, envelope, aggregateId, heavy, cancellationToken);

        public Task RecordAsync(Guid intentId, CommandEnvelope envelope, Guid? aggregateId, bool heavy, DateTimeOffset recordedAt, CancellationToken cancellationToken) =>
            inner.RecordAsync(intentId, envelope, aggregateId, heavy, recordedAt, cancellationToken);

        public Task<IReadOnlyList<RecordedIntent>> GetDueAsync(DateTimeOffset handedOverBefore, int batchSize, CancellationToken cancellationToken) =>
            inner.GetDueAsync(handedOverBefore, batchSize, cancellationToken);

        public Task<bool> TryClaimAsync(Guid intentId, DateTimeOffset? expectedLastHandedOverAt, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.TryClaimAsync(intentId, expectedLastHandedOverAt, now, cancellationToken);

        public Task<IReadOnlyList<Guid>> ClaimAsync(IReadOnlyList<RecordedIntent> due, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.ClaimAsync(due, now, cancellationToken);

        public Task RenewAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (renewals.Failing)
            {
                renewals.Refuse();
            }

            return inner.RenewAsync(intentId, now, cancellationToken);
        }

        public Task RenewManyAsync(IReadOnlyList<Guid> intentIds, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (renewals.Failing)
            {
                renewals.Refuse();
            }

            return inner.RenewManyAsync(intentIds, now, cancellationToken);
        }

        public Task<bool> TryRenewAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.TryRenewAsync(intentId, now, cancellationToken);

        public Task<bool> TryRenewFromAsync(Guid intentId, DateTimeOffset claimedAt, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.TryRenewFromAsync(intentId, claimedAt, now, cancellationToken);

        public Task RecordFailureAsync(Guid intentId, string failure, CancellationToken cancellationToken) =>
            inner.RecordFailureAsync(intentId, failure, cancellationToken);

        public Task RecordConflictAsync(Guid intentId, string failure, CancellationToken cancellationToken) =>
            inner.RecordConflictAsync(intentId, failure, cancellationToken);

        public Task ReturnAttemptAsync(Guid intentId, CancellationToken cancellationToken) =>
            inner.ReturnAttemptAsync(intentId, cancellationToken);

        public Task KeepAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.KeepAsync(intentId, now, cancellationToken);
    }
}
