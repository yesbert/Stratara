using Microsoft.EntityFrameworkCore;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
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
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Shared.Partitioning;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// Task 4.4: a full replay on a host whose projections read the store. The readers' checkpoints are back
/// at the beginning when the read models are emptied, and once the replay ends every read model is filled
/// again and the readers have advanced from the beginning.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ReplayWithStoreReadersTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const string ProjectionName = nameof(CounterViewProjection);
    private static readonly string ReaderName = $"postgres-transaction-id/{new CommitOrderOptions().PartitionCount}";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_full_replay_while_store_readers_run_refills_every_read_model_from_the_beginning()
    {
        var observed = new CheckpointsAtTruncation();
        using var app = await StartAsync(observed, siloPort: 11194, gatewayPort: 30083);
        var tenantId = Guid.NewGuid();
        var streams = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        observed.Partitions = [.. streams.Select(PartitionOf).Distinct()];

        foreach (var streamId in streams)
        {
            await AppendAsync(app.Services, tenantId, streamId, new CounterCreated(streamId));
            for (var i = 1; i <= 3; i++)
            {
                await AppendAsync(app.Services, tenantId, streamId, new CounterIncremented(streamId, i));
            }
        }

        foreach (var streamId in streams)
        {
            Assert.True(await WaitUntilAsync(async () => (await ReadViewAsync(app.Services, streamId))?.Value == 6), $"the view for {streamId} was not built before the replay");
        }

        var replay = app.Services.GetRequiredService<IProjectionReplayState>();
        replay.RequestReplay();
        Assert.True(await WaitUntilAsync(() => Task.FromResult(observed.Truncations > 0)), "the replay never emptied the read models");
        Assert.True(await WaitUntilAsync(() => Task.FromResult(!replay.IsReplayActive)), "the replay did not end");

        Assert.All(observed.Positions, position => Assert.Equal(0, position));
        foreach (var streamId in streams)
        {
            Assert.True(await WaitUntilAsync(async () => (await ReadViewAsync(app.Services, streamId))?.Value == 6), $"the view for {streamId} was not refilled after the replay");
        }

        await using var scope = app.Services.CreateAsyncScope();
        var checkpoints = scope.ServiceProvider.GetRequiredService<IProjectionCheckpointStore>();
        foreach (var partition in observed.Partitions)
        {
            Assert.True(
                await WaitUntilAsync(async () => await checkpoints.GetAsync(ProjectionName, partition, ReaderName) > 0),
                $"the reader of partition {partition} did not advance again after the replay");
        }

        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(CheckpointsAtTruncation observed, int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor("poc_replay_store"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.AddEventProjectionServices();
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(postgres.ConnectionStringFor("poc_replay_read"))
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddProjectionsFromAssemblyContaining<CounterViewProjection>()
            .AddSingleton(new ProjectionProbeControl())
            .AddSingleton(observed)
            .Configure<CommitOrderOptions>(options => options.MaintainPartitionCounter = false)
            .AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
            .AddStrataraProjectionCheckpoints<PocReadDbContext>()
            .AddScoped<IProjectionViewTruncator, ObservingViewTruncator>()
            .AddStrataraProjectionGrains(options =>
            {
                options.PollInterval = TimeSpan.FromSeconds(1);
                options.KeepAlivePeriod = TimeSpan.FromSeconds(5);
            });

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

    private static int PartitionOf(Guid streamId) => PartitionMap.PartitionOf(BucketCalculator.GetBucketId(streamId), new CommitOrderOptions().PartitionCount);

    private static async Task<CounterView?> ReadViewAsync(IServiceProvider services, Guid streamId)
    {
        await using var scope = services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
        return await context.CounterViews.AsNoTracking().SingleOrDefaultAsync(v => v.StreamId == streamId);
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

            await Task.Delay(200);
        }

        return false;
    }

    /// <summary>What the test's partitions' checkpoints held at the moment the read models were emptied.</summary>
    public sealed class CheckpointsAtTruncation
    {
        public IReadOnlyList<int> Partitions { get; set; } = [];

        public List<long> Positions { get; } = [];

        public int Truncations { get; set; }
    }

    /// <summary>The host's truncator: records the checkpoints it finds, then empties the probe read models.</summary>
    public sealed class ObservingViewTruncator(
        IDbContextFactory<PocReadDbContext> contextFactory,
        IProjectionCheckpointStore checkpoints,
        CheckpointsAtTruncation observed) : IProjectionViewTruncator
    {
        public async Task TruncateAllAsync(CancellationToken cancellationToken = default)
        {
            foreach (var partition in observed.Partitions)
            {
                observed.Positions.Add(await checkpoints.GetAsync(ProjectionName, partition, ReaderName, cancellationToken));
            }

            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await context.CounterViews.ExecuteDeleteAsync(cancellationToken);
            await context.CounterAudits.ExecuteDeleteAsync(cancellationToken);
            await context.CounterTotals.ExecuteDeleteAsync(cancellationToken);
            observed.Truncations++;
        }
    }
}
