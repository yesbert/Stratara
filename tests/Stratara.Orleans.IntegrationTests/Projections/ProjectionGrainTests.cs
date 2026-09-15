using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Session;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.Projections;
using Stratara.Shared.Partitioning;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// Task 8.1: the projection grain keeps every guarantee the <c>projections</c> capability states —
/// discovery by assembly, the recorded session, idempotent apply, a genuine failure that stops the
/// checkpoint, and a missing prerequisite that is retried and does not advance it — while reading the
/// store in commit order from a checkpoint instead of consuming the bus.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ProjectionGrainTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const string ProjectionName = nameof(CounterViewProjection);
    private static readonly string ReaderName = $"postgres-transaction-id/{new CommitOrderOptions().PartitionCount}";
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Events_reach_the_view_in_order_under_the_recorded_session_and_apply_once()
    {
        var control = new ProjectionProbeControl();
        using var app = await StartAsync(control, siloPort: 11191, gatewayPort: 30080);
        var tenantId = Guid.NewGuid();
        var streams = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();

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
            var view = await WaitForViewAsync(app.Services, streamId, v => v.Value == 6);
            Assert.Equal(4, view.LastVersion);
            Assert.Equal(tenantId, view.AppliedByTenant);
            Assert.Equal(4, view.Applications);
        }

        await using var checkpointScope = app.Services.CreateAsyncScope();
        var checkpoints = checkpointScope.ServiceProvider.GetRequiredService<IProjectionCheckpointStore>();
        var partitions = streams.Select(PartitionOf).Distinct().ToList();
        foreach (var partition in partitions)
        {
            Assert.True(await checkpoints.GetAsync(ProjectionName, partition, ReaderName) > 0, $"partition {partition} has no checkpoint");
        }

        foreach (var partition in partitions)
        {
            // A checkpoint changes behind a running grain only between a pause and a resume — the
            // way the rebuilder does it; the grain keeps its position otherwise.
            var grain = Grain(app.Services, partition);
            await grain.PauseAsync();
            await checkpoints.SetAsync(ProjectionName, partition, ReaderName, 0);
            await grain.ResumeAsync();
            await grain.CatchUpAsync();
        }

        foreach (var streamId in streams)
        {
            var view = await ReadViewAsync(app.Services, streamId);
            Assert.Equal(6, view!.Value);
            Assert.Equal(4, view.Applications);
        }

        await app.StopAsync();
    }

    [Fact]
    public async Task A_genuine_failure_stops_the_checkpoint_until_it_is_gone()
    {
        var control = new ProjectionProbeControl();
        using var app = await StartAsync(control, siloPort: 11192, gatewayPort: 30081);
        var tenantId = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        var partition = PartitionOf(streamId);
        await using var checkpointScope = app.Services.CreateAsyncScope();
        var checkpoints = checkpointScope.ServiceProvider.GetRequiredService<IProjectionCheckpointStore>();
        var before = await checkpoints.GetAsync(ProjectionName, partition, ReaderName);

        control.Poisoned[streamId] = 0;
        await AppendAsync(app.Services, tenantId, streamId, new CounterCreated(streamId));
        await Grain(app.Services, partition).CatchUpAsync();

        Assert.Null(await ReadViewAsync(app.Services, streamId));
        Assert.Equal(before, await checkpoints.GetAsync(ProjectionName, partition, ReaderName));

        control.Poisoned.TryRemove(streamId, out _);
        await Grain(app.Services, partition).CatchUpAsync();

        Assert.NotNull(await ReadViewAsync(app.Services, streamId));
        Assert.True(await checkpoints.GetAsync(ProjectionName, partition, ReaderName) > before);
        await app.StopAsync();
    }

    [Fact]
    public async Task A_missing_prerequisite_is_retried_and_does_not_advance_the_checkpoint()
    {
        var control = new ProjectionProbeControl();
        using var app = await StartAsync(control, siloPort: 11193, gatewayPort: 30082);
        var tenantId = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        var partition = PartitionOf(streamId);
        await using var checkpointScope = app.Services.CreateAsyncScope();
        var checkpoints = checkpointScope.ServiceProvider.GetRequiredService<IProjectionCheckpointStore>();
        var before = await checkpoints.GetAsync(ProjectionName, partition, ReaderName);

        control.AwaitingPrerequisite[streamId] = 0;
        await AppendAsync(app.Services, tenantId, streamId, new CounterCreated(streamId));
        await Grain(app.Services, partition).CatchUpAsync();

        Assert.True(control.PrerequisiteReports.GetValueOrDefault(streamId) > 1, "the missing prerequisite was not retried");
        Assert.Null(await ReadViewAsync(app.Services, streamId));
        Assert.Equal(before, await checkpoints.GetAsync(ProjectionName, partition, ReaderName));

        control.Prerequisites[streamId] = 0;
        await Grain(app.Services, partition).CatchUpAsync();

        Assert.NotNull(await ReadViewAsync(app.Services, streamId));
        Assert.True(await checkpoints.GetAsync(ProjectionName, partition, ReaderName) > before);
        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(ProjectionProbeControl control, int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor("poc_projection_store"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.AddEventProjectionWorkerServices();
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(postgres.ConnectionStringFor("poc_projection_read"))
            .AddAggregatesFromAssemblyContaining<ProjectionGrainTests>()
            .AddTrustedType<Counter>()
            .AddProjectionsFromAssemblyContaining<ProjectionGrainTests>()
            .AddSingleton(control)
            .Configure<CommitOrderOptions>(options => options.MaintainPartitionCounter = false)
            .AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
            .AddStrataraProjectionCheckpoints<PocReadDbContext>()
            .AddStrataraProjectionGrains(options =>
            {
                options.PollInterval = TimeSpan.FromSeconds(2);
                options.KeepAlivePeriod = TimeSpan.FromSeconds(5);
            });

        var app = builder.Build();
        await EnsureSchemaAsync(app.Services);
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static async Task EnsureSchemaAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await using (var write = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocCommitOrderWriteDbContext>>().CreateDbContextAsync())
        {
            await write.Database.EnsureCreatedAsync();
        }

        await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
        await read.Database.EnsureCreatedAsync();
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

    private static IProjectionGrain Grain(IServiceProvider services, int partition) =>
        services.GetRequiredService<IGrainFactory>().GetGrain<IProjectionGrain>(StoreReaderGrainKey.Of(ProjectionName, partition));

    private static int PartitionOf(Guid streamId) => PartitionMap.PartitionOf(BucketCalculator.GetBucketId(streamId), new CommitOrderOptions().PartitionCount);

    private static async Task<CounterView?> ReadViewAsync(IServiceProvider services, Guid streamId)
    {
        await using var scope = services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
        return await context.CounterViews.AsNoTracking().SingleOrDefaultAsync(v => v.StreamId == streamId);
    }

    private static async Task<CounterView> WaitForViewAsync(IServiceProvider services, Guid streamId, Func<CounterView, bool> ready)
    {
        var deadline = DateTimeOffset.UtcNow + ApplyTimeout;
        CounterView? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await ReadViewAsync(services, streamId);
            if (last is not null && ready(last))
            {
                return last;
            }

            await Task.Delay(200);
        }

        Assert.Fail($"The view for {streamId} did not reach the expected state within {ApplyTimeout}; last seen: value {last?.Value}, version {last?.LastVersion}.");
        return last!;
    }
}
