using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Session;
using Stratara.Diagnostics;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Checkpoints;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.Sagas;
using Stratara.Sagas.Abstractions;
using Stratara.Shared.Partitioning;

namespace Stratara.Orleans.IntegrationTests.Sagas;

/// <summary>
/// Each store-reading saga reads with a checkpoint of its own: a saga that fails on a fact stops only itself, and the
/// others apply the fact once and go on; a saga added to a running deployment starts where the host's sagas read; and
/// a deployment whose sagas shared one checkpoint starts every saga there, while the shared reader retires.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class SagaIsolationTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private static readonly string ReaderName = $"postgres-transaction-id/{new CommitOrderOptions().PartitionCount}";
    private static readonly string Steady = SagaReaderGrain.ConsumerOf(nameof(SteadySaga));
    private static readonly string Failing = SagaReaderGrain.ConsumerOf(nameof(FailingSaga));
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_saga_that_fails_on_a_fact_stops_only_itself_and_the_other_applies_it_once()
    {
        var log = new IsolationLog();
        var logs = new CapturedLogs();
        using var app = await StartAsync("poc_saga_isolation", log, logs, [typeof(SteadySaga), typeof(FailingSaga)], siloPort: 11471, gatewayPort: 30471);
        var tenantId = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        var partition = PartitionOf(streamId);
        using var stalled = new StallGauge(Failing, partition);
        log.Poisoned[streamId] = true;

        await AppendAsync(app.Services, tenantId, streamId, new CounterCreated(streamId));
        await AppendAsync(app.Services, tenantId, streamId, new CounterIncremented(streamId, 1));
        await AppendAsync(app.Services, tenantId, streamId, new CounterIncremented(streamId, 2));

        await WaitForAsync(() => log.Seen(nameof(SteadySaga), streamId).Count == 3, "the steady saga did not apply all three facts");
        await WaitForAsync(() => log.Attempts.GetValueOrDefault(streamId) >= 3, "the failing saga was not retried");

        Assert.Equal(["created", "incremented:1", "incremented:2"], log.Seen(nameof(SteadySaga), streamId));
        Assert.Equal(["created"], log.Seen(nameof(FailingSaga), streamId));
        Assert.Equal(1, stalled.Current);
        Assert.Contains(logs.Entries, entry =>
            entry.EventId == LogEvents.Orleans.PartitionStalled
            && entry.Message.Contains(Failing, StringComparison.Ordinal)
            && entry.Message.Contains($"partition {partition} ", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, entry =>
            entry.EventId == LogEvents.Orleans.PartitionStalled && entry.Message.Contains(Steady, StringComparison.Ordinal));
        Assert.True(await CheckpointAsync(app.Services, Failing, partition) < await CheckpointAsync(app.Services, Steady, partition));

        log.Poisoned.TryRemove(streamId, out _);
        await WaitForAsync(() => log.Seen(nameof(FailingSaga), streamId).Count == 3, "the failing saga did not go on once it could");

        Assert.Equal(["created", "incremented:1", "incremented:2"], log.Seen(nameof(FailingSaga), streamId));
        Assert.Equal(["created", "incremented:1", "incremented:2"], log.Seen(nameof(SteadySaga), streamId));
        await WaitForAsync(() => stalled.Current == 0, "the stall was not withdrawn once the saga went on");
        await StopAsync(app);
    }

    [Fact]
    public async Task A_saga_added_to_a_running_deployment_reacts_only_to_facts_after_the_host_s_sagas()
    {
        const string database = "poc_saga_added";
        var tenantId = Guid.NewGuid();
        var earlier = Guid.NewGuid();
        var later = Guid.NewGuid();

        var before = new IsolationLog();
        using (var app = await StartAsync(database, before, new CapturedLogs(), [typeof(SteadySaga)], siloPort: 11472, gatewayPort: 30472))
        {
            await AppendAsync(app.Services, tenantId, earlier, new CounterCreated(earlier));
            await WaitForAsync(() => before.Seen(nameof(SteadySaga), earlier).Count == 1, "the steady saga did not apply the earlier fact");
            await StopAsync(app);
        }

        var after = new IsolationLog();
        using (var app = await StartAsync(database, after, new CapturedLogs(), [typeof(SteadySaga), typeof(LateSaga)], siloPort: 11473, gatewayPort: 30473))
        {
            await AppendAsync(app.Services, tenantId, earlier, new CounterIncremented(earlier, 1));
            await AppendAsync(app.Services, tenantId, later, new CounterCreated(later));
            await WaitForAsync(
                () => after.Seen(nameof(LateSaga), earlier).Count == 1 && after.Seen(nameof(LateSaga), later).Count == 1
                      && after.Seen(nameof(SteadySaga), earlier).Count == 1 && after.Seen(nameof(SteadySaga), later).Count == 1,
                "the sagas did not apply the later facts");
            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.Equal(["incremented:1"], after.Seen(nameof(LateSaga), earlier));
            Assert.Equal(["created"], after.Seen(nameof(LateSaga), later));
            Assert.Equal(["incremented:1"], after.Seen(nameof(SteadySaga), earlier));
            await StopAsync(app);
        }
    }

    [Fact]
    public async Task Every_saga_starts_at_the_checkpoint_the_sagas_shared_and_the_shared_reader_retires()
    {
        const string database = "poc_saga_shared";
        var tenantId = Guid.NewGuid();
        var earlier = Guid.NewGuid();
        var later = Guid.NewGuid();
        var partitions = new CommitOrderOptions().PartitionCount;
        var shared = new long[partitions];

        var before = new IsolationLog();
        using (var app = await StartAsync(database, before, new CapturedLogs(), [typeof(SteadySaga)], siloPort: 11474, gatewayPort: 30474))
        {
            await AppendAsync(app.Services, tenantId, earlier, new CounterCreated(earlier));
            await AppendAsync(app.Services, tenantId, earlier, new CounterIncremented(earlier, 1));
            await WaitForAsync(() => before.Seen(nameof(SteadySaga), earlier).Count == 2, "the steady saga did not apply the earlier facts");
            await WaitForAsync(async () => await CheckpointAsync(app.Services, Steady, PartitionOf(earlier)) > 0, "the steady saga did not record its checkpoint");
            for (var partition = 0; partition < partitions; partition++)
            {
                shared[partition] = await CheckpointAsync(app.Services, Steady, partition);
            }

            await StopAsync(app);
        }

        var after = new IsolationLog();
        var logs = new CapturedLogs();
        using (var app = await StartAsync(database, after, logs, [typeof(SteadySaga), typeof(LateSaga)], siloPort: 11475, gatewayPort: 30475, services => AsTheSharedCheckpointAsync(services, shared)))
        {
            await AppendAsync(app.Services, tenantId, later, new CounterCreated(later));
            await WaitForAsync(
                () => after.Seen(nameof(LateSaga), later).Count == 1 && after.Seen(nameof(SteadySaga), later).Count == 1,
                "the sagas did not apply the later fact");

            var legacy = app.Services.GetRequiredService<IGrainFactory>().GetGrain<ISagaGrain>($"{SagaGrain.ConsumerName}/{PartitionOf(earlier)}");
            var reminders = app.Services.GetRequiredService<IReminderTable>();
            await reminders.UpsertRow(new ReminderEntry
            {
                GrainId = legacy.GetGrainId(),
                ReminderName = "keep-alive",
                StartAt = DateTime.UtcNow.AddSeconds(1),
                Period = TimeSpan.FromSeconds(5),
            });
            await WaitForAsync(
                async () => (await reminders.ReadRows(legacy.GetGrainId())).Reminders.Count == 0,
                "the shared reader's keep-alive was not unregistered");
            await legacy.EnsureRunningAsync();
            Assert.Equal(0, await legacy.CatchUpAsync());
            Assert.Empty((await reminders.ReadRows(legacy.GetGrainId())).Reminders);
            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.Empty(after.Seen(nameof(SteadySaga), earlier));
            Assert.Empty(after.Seen(nameof(LateSaga), earlier));
            Assert.Equal(["created"], after.Seen(nameof(SteadySaga), later));
            Assert.Equal(["created"], after.Seen(nameof(LateSaga), later));
            Assert.Contains(logs.Entries, entry => entry.EventId == LogEvents.Orleans.SharedSagaReaderRetired);
            Assert.DoesNotContain(logs.Entries, entry =>
                entry.EventId == LogEvents.Orleans.StoreReaderStarted && entry.Message.Contains($"for {SagaGrain.ConsumerName} ", StringComparison.Ordinal));
            Assert.Equal(shared[PartitionOf(earlier)], await CheckpointAsync(app.Services, SagaGrain.ConsumerName, PartitionOf(earlier)));
            await StopAsync(app);
        }
    }

    /// <summary>What a deployment whose sagas shared one checkpoint leaves: the shared checkpoint, and none of the saga's own.</summary>
    private static async Task AsTheSharedCheckpointAsync(IServiceProvider services, long[] positions)
    {
        await using var context = await services.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
        await context.Set<ProjectionCheckpoint>().Where(c => c.Projection.StartsWith(SagaReaderGrain.ConsumerPrefix)).ExecuteDeleteAsync();
        for (var partition = 0; partition < positions.Length; partition++)
        {
            if (positions[partition] > 0)
            {
                context.Set<ProjectionCheckpoint>().Add(new ProjectionCheckpoint { Projection = SagaGrain.ConsumerName, Partition = partition, Position = positions[partition], Reader = ReaderName });
            }
        }

        await context.SaveChangesAsync();
    }

    private async Task<IHost> StartAsync(
        string database,
        IsolationLog log,
        CapturedLogs logs,
        Type[] sagas,
        int siloPort,
        int gatewayPort,
        Func<IServiceProvider, Task>? beforeStart = null)
    {
        NpgsqlConnection.ClearAllPools();
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = Pruned(postgres.ConnectionStringFor($"{database}_store")),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.Logging.AddProvider(logs);
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.AddSagaServices();
        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(Pruned(postgres.ConnectionStringFor($"{database}_read")))
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddTrustedType<CounterIncremented>()
            .AddSingleton(log)
            .Configure<PocCounterOptions>(options => options.MaintainPartitionCounter = false)
            .AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
            .AddStrataraProjectionCheckpoints<PocReadDbContext>()
            .AddStrataraSagaGrains(options =>
            {
                options.PollInterval = TimeSpan.FromSeconds(1);
                options.KeepAlivePeriod = TimeSpan.FromSeconds(5);
            });
        foreach (var saga in sagas)
        {
            builder.Services.AddScoped(typeof(ISaga), saga);
        }

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using (var write = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocCommitOrderWriteDbContext>>().CreateDbContextAsync())
            {
                await write.Database.EnsureCreatedAsync();
            }

            await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
            await read.Database.EnsureCreatedAsync();
        }

        if (beforeStart is not null)
        {
            await beforeStart(app.Services);
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    /// <summary>
    /// Stops the host and returns the idle connections of every pool to the server, as each start does too: every host
    /// of the collection reads databases of its own, a saga host with a reader per saga and partition, and the stopped
    /// hosts' pools would otherwise hold the shared server's connection limit.
    /// </summary>
    private static async Task StopAsync(IHost app)
    {
        await app.StopAsync();
        NpgsqlConnection.ClearAllPools();
    }

    /// <summary>
    /// The connection string with idle connections closed within seconds: a saga host reads with a reader per saga and
    /// partition, and the collection's hosts share one server whose connection limit idle pools would otherwise hold.
    /// </summary>
    private static string Pruned(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { ConnectionIdleLifetime = 2, ConnectionPruningInterval = 1 }.ConnectionString;

    private static async Task AppendAsync(IServiceProvider services, Guid tenantId, Guid streamId, object @event)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
        var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
        if (@event is CounterCreated)
        {
            await events.CreateAsync<Counter>(streamId, @event);
        }
        else
        {
            await events.AppendAsync<Counter>(streamId, @event);
        }

        await events.SaveChangesAsync();
    }

    private static async Task<long> CheckpointAsync(IServiceProvider services, string consumer, int partition)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IProjectionCheckpointStore>().GetAsync(consumer, partition, ReaderName);
    }

    private static int PartitionOf(Guid streamId) => PartitionMap.PartitionOf(BucketCalculator.GetBucketId(streamId), new CommitOrderOptions().PartitionCount);

    private static Task WaitForAsync(Func<bool> ready, string failure) => WaitForAsync(() => Task.FromResult(ready()), failure);

    private static async Task WaitForAsync(Func<Task<bool>> ready, string failure)
    {
        var deadline = DateTimeOffset.UtcNow + ApplyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await ready())
            {
                return;
            }

            await Task.Delay(200);
        }

        Assert.Fail($"{failure} within {ApplyTimeout}.");
    }

    /// <summary>The stall counter's running value for one consumer and partition; the meter is process-wide, so the tags filter.</summary>
    private sealed class StallGauge : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _current;

        public StallGauge(string consumer, int partition)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ApplicationDiagnostics.Metrics.MeterName && instrument.Name == ApplicationDiagnostics.Metrics.OrleansReaderStalledName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
            {
                string? tagConsumer = null;
                int? tagPartition = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == ApplicationDiagnostics.MetricTags.Projection)
                    {
                        tagConsumer = tag.Value as string;
                    }
                    else if (tag.Key == ApplicationDiagnostics.MetricTags.Partition)
                    {
                        tagPartition = tag.Value as int?;
                    }
                }

                if (tagConsumer == consumer && tagPartition == partition)
                {
                    Interlocked.Add(ref _current, measurement);
                }
            });
            _listener.Start();
        }

        public long Current => Interlocked.Read(ref _current);

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Every log entry the host writes, with its event id and rendered message.</summary>
    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<(int EventId, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(Entries);

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<(int EventId, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((eventId.Id, formatter(state, exception)));
        }
    }
}

/// <summary>What each saga applied, per stream and in order, and the streams on which the failing saga throws, with how often it tried.</summary>
public sealed class IsolationLog
{
    private readonly ConcurrentDictionary<(string Saga, Guid Stream), ConcurrentQueue<string>> _seen = new();

    public ConcurrentDictionary<Guid, bool> Poisoned { get; } = new();

    public ConcurrentDictionary<Guid, int> Attempts { get; } = new();

    public void Record(string saga, Guid streamId, string step) =>
        _seen.GetOrAdd((saga, streamId), _ => new ConcurrentQueue<string>()).Enqueue(step);

    public IReadOnlyList<string> Seen(string saga, Guid streamId) =>
        _seen.TryGetValue((saga, streamId), out var queue) ? [.. queue] : [];
}

/// <summary>A saga that applies every fact it is handed.</summary>
public sealed class SteadySaga(IsolationLog log) : ISaga
{
    [UsedImplicitly]
    public Task HandleAsync(CounterCreated @event, CancellationToken cancellationToken)
    {
        log.Record(nameof(SteadySaga), @event.CounterId, "created");
        return Task.CompletedTask;
    }

    [UsedImplicitly]
    public Task HandleAsync(CounterIncremented @event, CancellationToken cancellationToken)
    {
        log.Record(nameof(SteadySaga), @event.CounterId, $"incremented:{@event.By}");
        return Task.CompletedTask;
    }
}

/// <summary>A saga that throws on every increment of a poisoned stream, for as long as it is poisoned.</summary>
public sealed class FailingSaga(IsolationLog log) : ISaga
{
    [UsedImplicitly]
    public Task HandleAsync(CounterCreated @event, CancellationToken cancellationToken)
    {
        log.Record(nameof(FailingSaga), @event.CounterId, "created");
        return Task.CompletedTask;
    }

    [UsedImplicitly]
    public Task HandleAsync(CounterIncremented @event, CancellationToken cancellationToken)
    {
        if (log.Poisoned.ContainsKey(@event.CounterId))
        {
            log.Attempts.AddOrUpdate(@event.CounterId, 1, (_, attempts) => attempts + 1);
            throw new InvalidOperationException($"The failing saga refuses increment {@event.By} of {@event.CounterId}.");
        }

        log.Record(nameof(FailingSaga), @event.CounterId, $"incremented:{@event.By}");
        return Task.CompletedTask;
    }
}

/// <summary>A saga registered after the others.</summary>
public sealed class LateSaga(IsolationLog log) : ISaga
{
    [UsedImplicitly]
    public Task HandleAsync(CounterCreated @event, CancellationToken cancellationToken)
    {
        log.Record(nameof(LateSaga), @event.CounterId, "created");
        return Task.CompletedTask;
    }

    [UsedImplicitly]
    public Task HandleAsync(CounterIncremented @event, CancellationToken cancellationToken)
    {
        log.Record(nameof(LateSaga), @event.CounterId, $"incremented:{@event.By}");
        return Task.CompletedTask;
    }
}
