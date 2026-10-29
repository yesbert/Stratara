using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Messages;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// Scenarios <em>An idle silo</em> and <em>A wake-up is lost</em>: a host with several projections in every partition
/// and a short poll. While nothing is committed, the readers read no entries and the store answers a few head questions
/// per partition and poll interval; a fact committed with every wake-up suppressed is applied by the poll. Run with the
/// native reader and with the portable reader.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class IdlePollTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int SiloPort = 11561;
    private const int GatewayPort = 30561;
    private const int Intervals = 10;
    private static readonly int PartitionCount = new CommitOrderOptions().PartitionCount;
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_idle_silo_asks_the_store_once_per_partition_rather_than_once_per_reader(bool portable)
    {
        var counts = new StoreCalls();
        var seen = new Seen();
        using var app = await StartAsync(counts, seen, portable);
        await Task.Delay(Poll * 4, TestContext.Current.CancellationToken);
        counts.Reset();

        await Task.Delay(Poll * Intervals, TestContext.Current.CancellationToken);

        Assert.Equal(0, counts.Reads);
        Assert.True(counts.Heads <= PartitionCount * 2 * (Intervals + 1),
            $"the store answered {counts.Heads} head questions for {PartitionCount} partitions over {Intervals} poll intervals");
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fact_whose_wake_up_was_lost_is_applied_by_the_poll(bool portable)
    {
        var seen = new Seen();
        using var app = await StartAsync(new StoreCalls(), seen, portable);
        await Task.Delay(Poll * 4, TestContext.Current.CancellationToken);

        var streamId = Guid.NewGuid();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(Guid.NewGuid()));
            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
            await events.CreateAsync<Counter>(streamId, new CounterCreated(streamId));
            await events.SaveChangesAsync();
        }

        var committedAt = DateTimeOffset.UtcNow;
        var deadline = committedAt + TimeSpan.FromSeconds(30);
        while (!seen.Saw(streamId) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(seen.Saw(streamId), "the poll did not apply the fact");
        var took = seen.When(streamId) - committedAt;
        Assert.True(took <= Poll * 1.5 + TimeSpan.FromSeconds(1), $"the poll applied the fact {took} after its commit");
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    private async Task<IHost> StartAsync(StoreCalls counts, Seen seen, bool portable)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor("poc_idle_poll_store"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, SiloPort, GatewayPort));
        builder.AddEventProjectionServices();
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(postgres.ConnectionStringFor("poc_idle_poll_read"))
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddSingleton(seen)
            .AddSingleton(counts)
            .AddScoped<IProjection, SeeingProbe>()
            .AddScoped<IProjection, IdleProbeA>()
            .AddScoped<IProjection, IdleProbeB>()
            .AddScoped<IProjection, IdleProbeC>()
            .AddScoped<IProjection, IdleProbeD>()
            .AddScoped<IProjection, IdleProbeE>()
            .Configure<PocCounterOptions>(options => options.MaintainPartitionCounter = portable)
            .AddStrataraProjectionCheckpoints<PocReadDbContext>()
            .AddStrataraProjectionGrains(options =>
            {
                options.PollInterval = Poll;
                options.KeepAlivePeriod = TimeSpan.FromMinutes(10);
            });
        if (portable)
        {
            builder.Services
                .AddScoped<PortableCounterReader<PocCommitOrderWriteDbContext>>()
                .AddScoped<ICommittedPositionReader>(services => new CountingReader(services.GetRequiredService<PortableCounterReader<PocCommitOrderWriteDbContext>>(), counts));
        }
        else
        {
            builder.Services
                .AddScoped<PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
                .AddScoped<ICommittedPositionReader>(services => new CountingReader(services.GetRequiredService<PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>(), counts));
        }

        builder.Services.AddSingleton<IEventBundleOutboxDispatcher, SilentDispatcher>();

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using (var write = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocCommitOrderWriteDbContext>>().CreateDbContextAsync())
            {
                await write.Database.EnsureCreatedAsync();
                await write.Database.ExecuteSqlRawAsync("DELETE FROM event_stream_entry");
                var counted = await write.Set<PartitionPosition>().Select(counter => counter.Partition).ToHashSetAsync();
                foreach (var partition in Enumerable.Range(0, PartitionCount).Where(partition => !counted.Contains(partition)))
                {
                    write.Set<PartitionPosition>().Add(new PartitionPosition { Partition = partition, Position = 0 });
                }

                await write.SaveChangesAsync();
            }

            await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
            await read.Database.EnsureCreatedAsync();
            await read.Database.ExecuteSqlRawAsync("DELETE FROM projection_checkpoint");
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    /// <summary>How often the store was read for entries and asked for a partition's head.</summary>
    private sealed class StoreCalls
    {
        private int _reads;
        private int _heads;

        public int Reads => Volatile.Read(ref _reads);

        public int Heads => Volatile.Read(ref _heads);

        public void Read() => Interlocked.Increment(ref _reads);

        public void Head() => Interlocked.Increment(ref _heads);

        public void Reset()
        {
            Interlocked.Exchange(ref _reads, 0);
            Interlocked.Exchange(ref _heads, 0);
        }
    }

    private sealed class CountingReader(ICommittedPositionReader inner, StoreCalls counts) : ICommittedPositionReader
    {
        public string Name => inner.Name;

        public Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default)
        {
            counts.Read();
            return inner.ReadAfterAsync(partition, afterPosition, batchSize, cancellationToken);
        }

        public Task<long> HeadAsync(int partition, CancellationToken cancellationToken = default)
        {
            counts.Head();
            return inner.HeadAsync(partition, cancellationToken);
        }

        public Task<long> CommittedHeadAsync(int partition, CancellationToken cancellationToken = default) => inner.CommittedHeadAsync(partition, cancellationToken);
    }

    /// <summary>A dispatcher that wakes nobody: every commit's wake-up is lost.</summary>
    private sealed class SilentDispatcher : IEventBundleOutboxDispatcher
    {
        public bool StoresBundlesWithCommit => false;

        public Task StoreEventBundleAsync(EventBundle eventBundle, Stratara.Abstractions.Persistence.ITransaction transaction, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EnqueueEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Seen
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _seen = new();

        public void Record(Guid streamId) => _seen.TryAdd(streamId, DateTimeOffset.UtcNow);

        public bool Saw(Guid streamId) => _seen.ContainsKey(streamId);

        public DateTimeOffset When(Guid streamId) => _seen[streamId];
    }

    private sealed class SeeingProbe(Seen seen) : IProjection
    {
        [UsedImplicitly]
        public Task HandleAsync(IEvent<CounterCreated> @event, CancellationToken cancellationToken)
        {
            seen.Record(@event.StreamId);
            return Task.CompletedTask;
        }
    }

    private abstract class IdleProbe : IProjection
    {
        [UsedImplicitly]
        public Task HandleAsync(IEvent<CounterCreated> @event, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class IdleProbeA : IdleProbe;

    private sealed class IdleProbeB : IdleProbe;

    private sealed class IdleProbeC : IdleProbe;

    private sealed class IdleProbeD : IdleProbe;

    private sealed class IdleProbeE : IdleProbe;
}
