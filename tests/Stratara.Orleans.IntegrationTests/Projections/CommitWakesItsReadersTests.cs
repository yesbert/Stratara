using System.Collections.Concurrent;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Session;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.Projections;
using Stratara.Projections.Abstractions;
using Stratara.Shared.Partitioning;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// Scenarios <em>A commit of an event one projection handles</em>, <em>A commit of an event no consumer handles</em>,
/// <em>The readers that applied nothing write their checkpoint later</em> and <em>A rebuild pauses a reader that holds a
/// position not yet written</em>: a host with one projection that handles the creation of a counter and several that
/// handle only its increments, one of them rebuildable. The poll and the keep-alive are far longer than
/// the test, so every read the store sees is one a commit's wake-up caused or the test asked for.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class CommitWakesItsReadersTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int SiloPort = 11560;
    private const int GatewayPort = 30560;
    private const int Commits = 10;
    private static readonly int PartitionCount = new CommitOrderOptions().PartitionCount;
    private static readonly string ReaderName = $"postgres-transaction-id/{PartitionCount}";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleCheckpoint = TimeSpan.FromSeconds(3);

    private static readonly string[] Bystanders =
    [
        nameof(IncrementProbeA), nameof(IncrementProbeB), nameof(IncrementProbeC), nameof(IncrementProbeD), nameof(IncrementProbeE),
    ];

    [Fact]
    public async Task A_commit_wakes_and_checkpoints_only_the_projection_that_handles_its_event()
    {
        var seen = new SeenLog();
        var reads = new ReadCount();
        using var app = await StartAsync(seen, reads);
        var partition = 0;
        var tenantId = Guid.NewGuid();
        var before = await CheckpointsAsync(app.Services, Bystanders, partition);
        await Task.Delay(Settle, TestContext.Current.CancellationToken);
        reads.Reset();

        var streams = await CreateInPartitionAsync(app.Services, tenantId, partition, Commits);
        Assert.True(await WaitUntilAsync(() => Task.FromResult(streams.All(seen.Saw))), "the handling projection did not apply every commit");
        await Task.Delay(Settle, TestContext.Current.CancellationToken);

        Assert.True(reads.Count <= 2 * Commits + 2,
            $"the store saw {reads.Count} reads for {Commits} commits that one of {Bystanders.Length + 1} projections handles");
        Assert.Equal(before, await CheckpointsAsync(app.Services, Bystanders, partition));
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_commit_of_an_event_nobody_handles_wakes_no_reader_and_writes_no_checkpoint()
    {
        var reads = new ReadCount();
        using var app = await StartAsync(new SeenLog(), reads);
        var partition = 1;
        var tenantId = Guid.NewGuid();
        await CreateInPartitionAsync(app.Services, tenantId, partition, 1);
        var consumers = Bystanders.Append(nameof(CreationProbe)).ToArray();
        await Task.Delay(Settle, TestContext.Current.CancellationToken);
        var before = await CheckpointsAsync(app.Services, consumers, partition);
        reads.Reset();

        await AppendUnhandledInPartitionAsync(app.Services, tenantId, partition, Commits);
        await Task.Delay(Settle, TestContext.Current.CancellationToken);

        Assert.Equal(0, reads.Count);
        Assert.Equal(before, await CheckpointsAsync(app.Services, consumers, partition));
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_reader_that_applied_nothing_writes_its_checkpoint_once_the_interval_has_passed()
    {
        var seen = new SeenLog();
        using var app = await StartAsync(seen, new ReadCount());
        var partition = 2;
        var tenantId = Guid.NewGuid();
        var bystander = Bystanders[0];
        var before = (await CheckpointsAsync(app.Services, [bystander], partition))[bystander];

        var streams = await CreateInPartitionAsync(app.Services, tenantId, partition, 3);
        Assert.True(await WaitUntilAsync(() => Task.FromResult(streams.All(seen.Saw))), "the handling projection did not apply the commits");
        var reader = app.Services.GetRequiredService<IGrainFactory>().GetGrain<IProjectionGrain>(StoreReaderGrainKey.Of(bystander, partition));
        await reader.CatchUpAsync();

        Assert.Equal(before, (await CheckpointsAsync(app.Services, [bystander], partition))[bystander]);
        var progress = app.Services.GetRequiredService<IProjectionProgress>();
        var mark = await progress.MarkAsync(streams, TestContext.Current.CancellationToken);
        var waited = await progress.WaitAsync(bystander, mark, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(ProjectionWaitOutcome.Reached, waited.Outcome);
        Assert.Equal(before, (await CheckpointsAsync(app.Services, [bystander], partition))[bystander]);

        await Task.Delay(IdleCheckpoint + TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await reader.CatchUpAsync();
        var after = (await CheckpointsAsync(app.Services, [bystander], partition))[bystander];
        Assert.True(after > (before ?? 0), $"the checkpoint stayed at {before} after the interval had passed");
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_rebuild_of_a_reader_holding_a_position_back_starts_from_the_beginning_and_applies_every_fact()
    {
        var seen = new SeenLog();
        var increments = new IncrementLog();
        using var app = await StartAsync(seen, new ReadCount(), increments);
        var partition = 3;
        var tenantId = Guid.NewGuid();
        var counted = await CreateInPartitionAsync(app.Services, tenantId, partition, 2);
        await IncrementAsync(app.Services, tenantId, counted);
        Assert.True(await WaitUntilAsync(() => Task.FromResult(counted.All(stream => increments.Applied(stream) == 1))), "the rebuildable projection did not apply the increments");

        var created = await CreateInPartitionAsync(app.Services, tenantId, partition, 2);
        Assert.True(await WaitUntilAsync(() => Task.FromResult(created.All(seen.Saw))), "the handling projection did not apply the creations");
        var reader = app.Services.GetRequiredService<IGrainFactory>().GetGrain<IProjectionGrain>(StoreReaderGrainKey.Of(nameof(RebuildableIncrementProbe), partition));
        await reader.CatchUpAsync();

        await app.Services.GetRequiredService<IProjectionRebuilder>().RebuildAsync(nameof(RebuildableIncrementProbe), TestContext.Current.CancellationToken);

        Assert.Equal(0L, increments.CheckpointWhenEmptied(partition) ?? 0L);
        Assert.True(await WaitUntilAsync(() => Task.FromResult(counted.All(stream => increments.Applied(stream) == 2))), "the rebuild did not apply every increment again");
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Increments each counter of <paramref name="streams"/> once, one commit each.</summary>
    private static async Task IncrementAsync(IServiceProvider services, Guid tenantId, IEnumerable<Guid> streams)
    {
        foreach (var streamId in streams)
        {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
            await events.AppendAsync<Counter>(streamId, new CounterIncremented(streamId, 1));
            await events.SaveChangesAsync();
        }
    }

    /// <summary>Creates <paramref name="count"/> counters in <paramref name="partition"/>, one commit each.</summary>
    private static async Task<List<Guid>> CreateInPartitionAsync(IServiceProvider services, Guid tenantId, int partition, int count)
    {
        var streams = StreamsIn(partition, count);
        foreach (var streamId in streams)
        {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
            await events.CreateAsync<Counter>(streamId, new CounterCreated(streamId));
            await events.SaveChangesAsync();
        }

        return streams;
    }

    /// <summary>Creates <paramref name="count"/> streams in <paramref name="partition"/> of an event type no projection handles.</summary>
    private static async Task AppendUnhandledInPartitionAsync(IServiceProvider services, Guid tenantId, int partition, int count)
    {
        foreach (var streamId in StreamsIn(partition, count))
        {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
            await events.CreateAsync<Counter>(streamId, new CounterNoted(streamId));
            await events.SaveChangesAsync();
        }
    }

    private static List<Guid> StreamsIn(int partition, int count)
    {
        var streams = new List<Guid>(count);
        while (streams.Count < count)
        {
            var streamId = Guid.NewGuid();
            if (PartitionMap.PartitionOf(BucketCalculator.GetBucketId(streamId), PartitionCount) == partition)
            {
                streams.Add(streamId);
            }
        }

        return streams;
    }

    private static async Task<Dictionary<string, long?>> CheckpointsAsync(IServiceProvider services, IEnumerable<string> consumers, int partition)
    {
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProjectionCheckpointStore>();
        var positions = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var consumer in consumers)
        {
            positions[consumer] = await store.FindAsync(consumer, partition, ReaderName);
        }

        return positions;
    }

    private async Task<IHost> StartAsync(SeenLog seen, ReadCount reads, IncrementLog? increments = null)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor("poc_commit_wakes_store"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, SiloPort, GatewayPort));
        builder.AddEventProjectionServices();
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(postgres.ConnectionStringFor("poc_commit_wakes_read"))
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddTrustedType<CounterIncremented>()
            .AddTrustedType<CounterNoted>()
            .AddSingleton(seen)
            .AddSingleton(reads)
            .AddSingleton(increments ?? new IncrementLog())
            .AddScoped<IProjection, CreationProbe>()
            .AddScoped<IProjection, RebuildableIncrementProbe>()
            .AddScoped<IProjection, IncrementProbeA>()
            .AddScoped<IProjection, IncrementProbeB>()
            .AddScoped<IProjection, IncrementProbeC>()
            .AddScoped<IProjection, IncrementProbeD>()
            .AddScoped<IProjection, IncrementProbeE>()
            .Configure<PocCounterOptions>(options => options.MaintainPartitionCounter = false)
            .AddScoped<PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
            .AddScoped<ICommittedPositionReader>(services => new CountingReader(services.GetRequiredService<PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>(), reads))
            .AddStrataraProjectionCheckpoints<PocReadDbContext>()
            .AddStrataraProjectionGrains(options =>
            {
                options.PollInterval = TimeSpan.FromMinutes(10);
                options.KeepAlivePeriod = TimeSpan.FromMinutes(10);
                options.IdleCheckpointInterval = IdleCheckpoint;
            });

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using (var write = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocCommitOrderWriteDbContext>>().CreateDbContextAsync())
            {
                await write.Database.EnsureCreatedAsync();
                await write.Database.ExecuteSqlRawAsync("DELETE FROM event_stream_entry");
            }

            await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
            await read.Database.EnsureCreatedAsync();
            await read.Database.ExecuteSqlRawAsync("DELETE FROM projection_checkpoint");
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(250);
        }

        return false;
    }

    /// <summary>How often the store was read for entries, by any reader.</summary>
    private sealed class ReadCount
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Add() => Interlocked.Increment(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);
    }

    /// <summary>The host's reader, counting every read of entries.</summary>
    private sealed class CountingReader(ICommittedPositionReader inner, ReadCount reads) : ICommittedPositionReader
    {
        public string Name => inner.Name;

        public Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default)
        {
            reads.Add();
            return inner.ReadAfterAsync(partition, afterPosition, batchSize, cancellationToken);
        }

        public Task<long> HeadAsync(int partition, CancellationToken cancellationToken = default) => inner.HeadAsync(partition, cancellationToken);

        public Task<long> CommittedHeadAsync(int partition, CancellationToken cancellationToken = default) => inner.CommittedHeadAsync(partition, cancellationToken);
    }

    /// <summary>Which streams the creation probe saw.</summary>
    private sealed class SeenLog
    {
        private readonly ConcurrentDictionary<Guid, byte> _seen = new();

        public void Record(Guid streamId) => _seen[streamId] = 0;

        public bool Saw(Guid streamId) => _seen.ContainsKey(streamId);
    }

    private sealed class CreationProbe(SeenLog seen) : IProjection
    {
        [UsedImplicitly]
        public Task HandleAsync(IEvent<CounterCreated> @event, CancellationToken cancellationToken)
        {
            seen.Record(@event.StreamId);
            return Task.CompletedTask;
        }
    }

    /// <summary>How often the rebuildable probe applied each stream's increment, and the checkpoints it found when it was emptied.</summary>
    private sealed class IncrementLog
    {
        private readonly ConcurrentDictionary<Guid, int> _applied = new();
        private readonly ConcurrentDictionary<int, long?> _emptiedAt = new();

        public void Apply(Guid streamId) => _applied.AddOrUpdate(streamId, 1, (_, count) => count + 1);

        public int Applied(Guid streamId) => _applied.GetValueOrDefault(streamId);

        public void Emptied(int partition, long? checkpoint) => _emptiedAt[partition] = checkpoint;

        public long? CheckpointWhenEmptied(int partition) => _emptiedAt.GetValueOrDefault(partition);
    }

    private sealed class RebuildableIncrementProbe(IncrementLog increments, IProjectionCheckpointStore checkpoints) : IRebuildableProjection
    {
        public async Task TruncateAsync(CancellationToken cancellationToken)
        {
            for (var partition = 0; partition < PartitionCount; partition++)
            {
                increments.Emptied(partition, await checkpoints.FindAsync(nameof(RebuildableIncrementProbe), partition, ReaderName, cancellationToken));
            }
        }

        [UsedImplicitly]
        public Task HandleAsync(IEvent<CounterIncremented> @event, CancellationToken cancellationToken)
        {
            increments.Apply(@event.StreamId);
            return Task.CompletedTask;
        }
    }

    private abstract class IncrementProbe : IProjection
    {
        [UsedImplicitly]
        public Task HandleAsync(IEvent<CounterIncremented> @event, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class IncrementProbeA : IncrementProbe;

    private sealed class IncrementProbeB : IncrementProbe;

    private sealed class IncrementProbeC : IncrementProbe;

    private sealed class IncrementProbeD : IncrementProbe;

    private sealed class IncrementProbeE : IncrementProbe;
}
