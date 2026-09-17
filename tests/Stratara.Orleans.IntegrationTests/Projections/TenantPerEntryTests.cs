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
using Stratara.Orleans.IntegrationTests.Sagas;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.Sagas;
using Stratara.Projections.Abstractions;
using Stratara.Sagas.Abstractions;
using Stratara.Shared.Partitioning;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// One read of the store returns facts of two tenants, interleaved in one partition; each is applied with the services
/// that apply it constructed under its own tenant — for a projection, for a saga, and for a process whose state is read
/// through a service that takes its tenant at construction (scenarios <em>One read returns entries of several
/// tenants</em>, <em>A fact reaches a process through a tenant-routed store</em>). The facts are appended while a replay
/// holds every reader back, so the readers find all of them in their first read.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class TenantPerEntryTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_projection_sees_each_entry_under_its_own_tenant()
    {
        var captures = new TenantCaptures();
        using var app = await StartAsync(captures, "poc_tenant_projection", sagas: false, siloPort: 11237, gatewayPort: 30126, services => services
            .AddScoped<IProjection, TenantCaptureProjection>()
            .AddScoped<TenantCaptureProjection>());

        var expected = await AppendInterleavedAsync(app.Services);

        var captured = await WaitForAsync(captures, expected.Count);
        Assert.Equal(expected, captured.OrderBy(c => c.StreamId).ThenBy(c => c.Step).ToList());
        await app.StopAsync();
    }

    [Fact]
    public async Task A_saga_sees_each_entry_under_its_own_tenant()
    {
        var captures = new TenantCaptures();
        using var app = await StartAsync(captures, "poc_tenant_saga", sagas: true, siloPort: 11238, gatewayPort: 30127, services => services
            .AddScoped<ISaga, TenantCaptureSaga>());

        var expected = await AppendInterleavedAsync(app.Services);

        var captured = await WaitForAsync(captures, expected.Count);
        Assert.Equal(expected, captured.OrderBy(c => c.StreamId).ThenBy(c => c.Step).ToList());
        await app.StopAsync();
    }

    [Fact]
    public async Task A_process_reads_its_state_under_the_tenant_of_the_fact_it_was_handed()
    {
        var captures = new TenantCaptures();
        using var app = await StartAsync(captures, "poc_tenant_process", sagas: true, siloPort: 11239, gatewayPort: 30128, services =>
        {
            services.AddScoped<ISaga, TenantProcess>().AddTrustedType<TenantProcessState>().AddTrustedType<TenantStepRecorded>();
            TenantCapturingAggregation.Decorate(services);
        });

        var tenants = await AppendInterleavedAsync(app.Services);

        var stateStreams = tenants.Select(t => t.StreamId).Distinct()
            .ToDictionary(stream => SagaProcessKey.StateStreamOf(nameof(TenantProcess), stream), stream => tenants.First(t => t.StreamId == stream).Tenant);
        var reads = await WaitForAsync(captures, stateStreams.Count, capture => stateStreams.ContainsKey(capture.StreamId));
        Assert.All(reads, read => Assert.Equal(stateStreams[read.StreamId], read.Tenant));
        await app.StopAsync();
    }

    /// <summary>
    /// Two streams of two tenants in one partition, appended in turn — created, created, incremented, incremented — while
    /// a replay holds the readers back; returns what each fact should be applied under.
    /// </summary>
    private static async Task<List<(Guid StreamId, string Step, Guid Tenant)>> AppendInterleavedAsync(IServiceProvider services)
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        while (PartitionOf(second) != PartitionOf(first))
        {
            second = Guid.NewGuid();
        }

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var replay = services.GetRequiredService<IProjectionReplayState>();
        replay.Activate();
        try
        {
            await AppendAsync(services, tenantA, first, new CounterCreated(first));
            await AppendAsync(services, tenantB, second, new CounterCreated(second));
            await AppendAsync(services, tenantA, first, new CounterIncremented(first, 1));
            await AppendAsync(services, tenantB, second, new CounterIncremented(second, 1));
        }
        finally
        {
            replay.Deactivate();
        }

        return
        [
            .. new[] { (first, "created", tenantA), (first, "incremented", tenantA), (second, "created", tenantB), (second, "incremented", tenantB) }
                .OrderBy(c => c.Item1).ThenBy(c => c.Item2),
        ];
    }

    private async Task<IHost> StartAsync(TenantCaptures captures, string database, bool sagas, int siloPort, int gatewayPort, Action<IServiceCollection> register)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor(database + "_store"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        if (sagas)
        {
            builder.AddSagaServices();
        }
        else
        {
            builder.AddEventProjectionServices();
            builder.Services.AddEventSourcing();
        }

        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(postgres.ConnectionStringFor(database + "_read"))
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddTrustedType<CounterIncremented>()
            .AddSingleton(captures)
            .AddScoped<TenantAtConstruction>()
            .Configure<PocCounterOptions>(options => options.MaintainPartitionCounter = false)
            .AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
            .AddStrataraProjectionCheckpoints<PocReadDbContext>();
        if (sagas)
        {
            builder.Services.AddStrataraSagaGrains(options => options.PollInterval = TimeSpan.FromSeconds(1));
        }
        else
        {
            builder.Services.AddStrataraProjectionGrains(options => options.PollInterval = TimeSpan.FromSeconds(1));
        }

        register(builder.Services);

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

    private static async Task<List<(Guid StreamId, string Step, Guid Tenant)>> WaitForAsync(TenantCaptures captures, int count, Func<(Guid StreamId, string Step, Guid Tenant), bool>? filter = null)
    {
        var deadline = DateTimeOffset.UtcNow + ApplyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var seen = captures.All.Where(filter ?? (_ => true)).ToList();
            if (seen.Count >= count)
            {
                return seen;
            }

            await Task.Delay(200);
        }

        Assert.Fail($"Only {captures.All.Count} applications were seen within {ApplyTimeout}: {string.Join(", ", captures.All)}");
        return [];
    }
}
