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
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// Task 5.4: a projection rebuilt on a real store while its readers run. A truncation that empties the model and
/// then throws leaves the checkpoints at the beginning, so the readers re-read every fact and fill the model again
/// without a new fact arriving; a rebuild that succeeds does the same.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class RebuildEndToEndTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int Streams = 3;
    private const int FactsPerStream = 3;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_failed_and_a_successful_rebuild_both_refill_the_model_from_the_beginning()
    {
        var control = new RebuildProbeControl();
        var read = postgres.ConnectionStringFor("poc_rebuild_read");
        using var app = await StartAsync(control, read, siloPort: 11281, gatewayPort: 30171);
        var tenantId = Guid.NewGuid();

        foreach (var streamId in Enumerable.Range(0, Streams).Select(_ => Guid.NewGuid()))
        {
            await AppendAsync(app.Services, tenantId, streamId, new CounterCreated(streamId), create: true);
            for (var i = 1; i < FactsPerStream; i++)
            {
                await AppendAsync(app.Services, tenantId, streamId, new CounterIncremented(streamId, i), create: false);
            }
        }

        var expected = Streams * FactsPerStream;
        Assert.True(await WaitUntilAsync(async () => await CountAsync(app.Services) == expected), "the model was not built before the rebuild");
        var rebuilder = app.Services.GetRequiredService<IProjectionRebuilder>();

        control.FailTruncation = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rebuilder.RebuildAsync(nameof(RebuildProbeProjection)));
        Assert.True(control.Truncations >= 1, "the rebuild never reached the truncation");
        Assert.True(
            await WaitUntilAsync(async () => await CountAsync(app.Services) == expected),
            $"after the failed rebuild the readers did not re-read from the beginning; {await CountAsync(app.Services)} of {expected} rows");

        control.FailTruncation = false;
        await rebuilder.RebuildAsync(nameof(RebuildProbeProjection));
        Assert.True(
            await WaitUntilAsync(async () => await CountAsync(app.Services) == expected),
            $"after the rebuild the model was not refilled; {await CountAsync(app.Services)} of {expected} rows");
        Assert.Equal(2, control.Truncations);

        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(RebuildProbeControl control, string read, int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor("poc_rebuild_store"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.AddEventProjectionServices();
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(read)
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddTrustedType<CounterIncremented>()
            .AddScoped<IProjection, RebuildProbeProjection>()
            .AddSingleton(control)
            .Configure<CommitOrderOptions>(options => options.MaintainPartitionCounter = false)
            .AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
            .AddStrataraProjectionCheckpoints<PocReadDbContext>()
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

            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
            await context.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS poc_rebuild_probe (stream_id uuid NOT NULL, version bigint NOT NULL, PRIMARY KEY (stream_id, version))");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM poc_rebuild_probe");
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static async Task AppendAsync(IServiceProvider services, Guid tenantId, Guid streamId, object @event, bool create)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
        var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
        if (create)
        {
            await events.CreateAsync<Counter>(streamId, @event);
        }
        else
        {
            await events.AppendAsync<Counter>(streamId, @event);
        }

        await events.SaveChangesAsync();
    }

    private static async Task<long> CountAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
        return await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM poc_rebuild_probe").SingleAsync();
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
}

/// <summary>Whether the probe's next truncation fails after emptying the model, and how many truncations ran.</summary>
public sealed class RebuildProbeControl
{
    private int _truncations;

    public bool FailTruncation { get; set; }

    public int Truncations => _truncations;

    public void CountTruncation() => Interlocked.Increment(ref _truncations);
}

/// <summary>
/// One row per fact, idempotent by stream and version, in a table of its own. Every host of this assembly that
/// discovers its projections registers the probe as well; without a <see cref="RebuildProbeControl"/> it is
/// inert, so those hosts need neither its table nor its control.
/// </summary>
public sealed class RebuildProbeProjection(IServiceProvider services) : IRebuildableProjection
{
    private readonly RebuildProbeControl? _control = services.GetService<RebuildProbeControl>();

    public async Task TruncateAsync(CancellationToken cancellationToken)
    {
        if (_control is null)
        {
            return;
        }

        await using (var context = await ContextFactory().CreateDbContextAsync(cancellationToken))
        {
            await context.Database.ExecuteSqlRawAsync("DELETE FROM poc_rebuild_probe", cancellationToken);
        }

        _control.CountTruncation();
        if (_control.FailTruncation)
        {
            throw new InvalidOperationException("the probe's truncation failed after emptying the model");
        }
    }

    public Task HandleAsync(IEvent<CounterCreated> @event, CancellationToken cancellationToken) => RecordAsync(@event, cancellationToken);

    public Task HandleAsync(IEvent<CounterIncremented> @event, CancellationToken cancellationToken) => RecordAsync(@event, cancellationToken);

    private IDbContextFactory<PocReadDbContext> ContextFactory() => services.GetRequiredService<IDbContextFactory<PocReadDbContext>>();

    private async Task RecordAsync(IEvent @event, CancellationToken cancellationToken)
    {
        if (_control is null)
        {
            return;
        }

        await using var context = await ContextFactory().CreateDbContextAsync(cancellationToken);
        await context.Database.ExecuteSqlAsync(
            $"INSERT INTO poc_rebuild_probe (stream_id, version) VALUES ({@event.StreamId}, {@event.Version}) ON CONFLICT (stream_id, version) DO NOTHING",
            cancellationToken);
    }
}
