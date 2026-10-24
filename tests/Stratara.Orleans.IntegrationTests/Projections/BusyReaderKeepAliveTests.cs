using System.Collections.Concurrent;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Session;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Aggregates;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Projections.Abstractions;
using Stratara.Sagas.Abstractions;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// Scenarios <em>A projection catches up longer than the response timeout</em>, <em>A saga reader catches up longer
/// than the response timeout</em> and <em>A keep-alive during a catch-up reads once more</em>: under a two-second
/// response timeout and a one-second keep-alive, a reader whose first entry takes fifteen seconds to apply catches up
/// through a dozen keep-alive ticks and poll ticks, applies an entry committed meanwhile in the same catch-up, never
/// applies two entries at once, and the silo logs no reminder tick it could not deliver.
/// </summary>
/// <remarks>
/// The absence of the runtime's lines is read from their wording, which Orleans may change; the outcome — every entry
/// applied once, one at a time — is asserted beside it, as <see cref="LongHandOverTests"/> does.
/// </remarks>
[Collection(InfrastructureCollection.Name)]
public sealed class BusyReaderKeepAliveTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RemindersLoaded = PocSilo.RefreshReminderListPeriod + TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] RuntimeReminderErrors = ["Could not deliver reminder tick", "Response did not arrive on time"];

    [Fact]
    public async Task A_projection_that_catches_up_longer_than_the_response_timeout_answers_its_keep_alive()
    {
        var gate = new SlowEntryGate(Hold);
        var logs = new RuntimeLogs();
        using var app = await StartAsync(gate, logs, "poc_busy_projection", siloPort: 11530, gatewayPort: 30530, host => host
            .AddEventProjectionServicesFor()
            .AddScoped<IProjection, SlowProjection>()
            .AddStrataraProjectionGrains(options =>
            {
                options.PollInterval = TimeSpan.FromSeconds(1);
                options.KeepAlivePeriod = KeepAlive;
            }));

        await AssertBusyReaderAnswersAsync(app, gate, logs);
    }

    [Fact]
    public async Task A_saga_reader_that_catches_up_longer_than_the_response_timeout_answers_its_keep_alive()
    {
        var gate = new SlowEntryGate(Hold);
        var logs = new RuntimeLogs();
        using var app = await StartAsync(gate, logs, "poc_busy_saga", siloPort: 11531, gatewayPort: 30531, host => host
            .AddSagaServicesFor()
            .AddScoped<ISaga, SlowSaga>()
            .AddStrataraSagaGrains(options =>
            {
                options.PollInterval = TimeSpan.FromSeconds(1);
                options.KeepAlivePeriod = KeepAlive;
            }));

        await AssertBusyReaderAnswersAsync(app, gate, logs);
    }

    /// <summary>
    /// Commits the slow entry once the silo's reminders are loaded, a second entry into the same partition while the
    /// first holds the catch-up, and waits for both.
    /// </summary>
    private static async Task AssertBusyReaderAnswersAsync(IHost app, SlowEntryGate gate, RuntimeLogs logs)
    {
        await Task.Delay(RemindersLoaded, TestContext.Current.CancellationToken);
        var tenantId = Guid.NewGuid();
        await AppendAsync(app.Services, tenantId, gate.SlowStream);
        Assert.True(await WaitUntilAsync(() => gate.Started(gate.SlowStream)), "the slow entry was never applied");

        var meanwhile = StreamInPartitionOf(app.Services, gate.SlowStream);
        await AppendAsync(app.Services, tenantId, meanwhile);

        Assert.True(await WaitUntilAsync(() => gate.Applied(gate.SlowStream) && gate.Applied(meanwhile)), "the backlog was not applied");
        await Task.Delay(ResponseTimeout * 2, TestContext.Current.CancellationToken);

        Assert.Equal(1, gate.Count(gate.SlowStream));
        Assert.Equal(1, gate.Count(meanwhile));
        Assert.Equal(1, gate.MaxConcurrent);
        Assert.Empty(logs.Matching(RuntimeReminderErrors));
        await app.StopAsync();
    }

    /// <summary>A new stream whose entries go to the slow stream's partition, so the reader that holds it reads them.</summary>
    private static Guid StreamInPartitionOf(IServiceProvider services, Guid streamId)
    {
        var partitions = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CommitOrderOptions>>().Value.PartitionCount;
        var partition = PartitionOf(streamId, partitions);
        while (true)
        {
            var candidate = Guid.NewGuid();
            if (PartitionOf(candidate, partitions) == partition)
            {
                return candidate;
            }
        }
    }

    private static int PartitionOf(Guid streamId, int partitions) =>
        PartitionMap.PartitionOf(Stratara.Shared.Partitioning.BucketCalculator.GetBucketId(streamId), partitions);

    private async Task<IHost> StartAsync(SlowEntryGate gate, RuntimeLogs logs, string database, int siloPort, int gatewayPort, Action<HostApplicationBuilder> readers)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor(database + "_store"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.Logging.AddProvider(logs);
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort)
            .Configure<SiloMessagingOptions>(options => options.ResponseTimeout = ResponseTimeout));
        readers(builder);
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(postgres.ConnectionStringFor(database + "_read"))
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddTrustedType<CounterIncremented>()
            .AddSingleton(gate)
            .Configure<PocCounterOptions>(options => options.MaintainPartitionCounter = false)
            .AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
            .AddStrataraProjectionCheckpoints<PocReadDbContext>();

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

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static async Task AppendAsync(IServiceProvider services, Guid tenantId, Guid streamId)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
        var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
        await events.CreateAsync<Counter>(streamId, new CounterCreated(streamId));
        await events.SaveChangesAsync();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + SettleTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>
    /// Holds the entry of one stream for a fixed time and counts every entry applied, and how many were applied at once.
    /// The streams of earlier tests in the shared store are counted too and change nothing.
    /// </summary>
    public sealed class SlowEntryGate(TimeSpan hold)
    {
        private readonly ConcurrentDictionary<Guid, int> _started = new();
        private readonly ConcurrentDictionary<Guid, int> _applied = new();
        private int _inFlight;
        private int _maxConcurrent;

        public Guid SlowStream { get; } = Guid.NewGuid();

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public bool Started(Guid streamId) => _started.ContainsKey(streamId);

        public bool Applied(Guid streamId) => _applied.ContainsKey(streamId);

        public int Count(Guid streamId) => _applied.GetValueOrDefault(streamId);

        public async Task ApplyAsync(Guid streamId)
        {
            var inFlight = Interlocked.Increment(ref _inFlight);
            InterlockedMax(inFlight);
            _started.AddOrUpdate(streamId, 1, static (_, count) => count + 1);
            try
            {
                if (streamId == SlowStream)
                {
                    await Task.Delay(hold);
                }

                _applied.AddOrUpdate(streamId, 1, static (_, count) => count + 1);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private void InterlockedMax(int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref _maxConcurrent)))
            {
                Interlocked.CompareExchange(ref _maxConcurrent, value, current);
            }
        }
    }

    public sealed class SlowProjection(SlowEntryGate gate) : IProjection
    {
        [UsedImplicitly]
        public Task HandleAsync(IEvent<CounterCreated> @event, CancellationToken cancellationToken) => gate.ApplyAsync(@event.StreamId);
    }

    public sealed class SlowSaga(SlowEntryGate gate) : ISaga
    {
        [UsedImplicitly]
        public Task HandleAsync(CounterCreated @event, CancellationToken cancellationToken) => gate.ApplyAsync(@event.CounterId);
    }
}

/// <summary>The reader registrations of the scenario hosts, so one test class can host either.</summary>
internal static class BusyReaderRegistrations
{
    public static IServiceCollection AddEventProjectionServicesFor(this HostApplicationBuilder builder)
    {
        builder.AddEventProjectionServices();
        return builder.Services;
    }

    public static IServiceCollection AddSagaServicesFor(this HostApplicationBuilder builder)
    {
        builder.AddSagaServices();
        return builder.Services;
    }
}
