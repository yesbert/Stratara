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
/// Scenario <em>A projection is asked to rebuild twice at once</em>: two rebuilds of one projection overlap — the
/// second truncates while the first has already resumed the readers. With a pause that was a flag, the readers
/// re-read and advanced their checkpoints between the two truncations, and the second truncation emptied the model
/// for good. With a pause that is a count, the readers resume only when both have finished, and the model is
/// complete once they have caught up.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class OverlappingRebuildTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int Streams = 4;
    private const int FactsPerStream = 3;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task Two_overlapping_rebuilds_leave_the_model_complete()
    {
        var control = new RebuildProbeControl();
        var read = postgres.ConnectionStringFor("poc_overlap_read");
        using var app = await StartAsync(control, read, siloPort: 11314, gatewayPort: 30204);
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
        Assert.True(await WaitUntilAsync(async () => await CountAsync(app.Services) == expected), "the model was not built before the rebuilds");
        var rebuilder = app.Services.GetRequiredService<IProjectionRebuilder>();

        control.HoldTruncationNumber = 2;
        var first = rebuilder.RebuildAsync(nameof(RebuildProbeProjection));
        var second = rebuilder.RebuildAsync(nameof(RebuildProbeProjection));
        await Task.WhenAny(first, second);
        Assert.True(await WaitUntilAsync(() => Task.FromResult(control.Truncations == 2)), "the second truncation was never reached");
        await Task.Delay(Settle);
        var rowsWhileTheSecondStillHeld = await CountAsync(app.Services);
        control.HoldTruncation.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.True(
            await WaitUntilAsync(async () => await CountAsync(app.Services) == expected),
            $"after two overlapping rebuilds the model holds {await CountAsync(app.Services)} of {expected} rows ({rowsWhileTheSecondStillHeld} while the second rebuild still held its truncation)");
        await Task.Delay(Settle);
        Assert.Equal(expected, await CountAsync(app.Services));
        TestContext.Current.TestOutputHelper?.WriteLine($"{rowsWhileTheSecondStillHeld} rows while the second rebuild held its truncation, {expected} after both");

        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(RebuildProbeControl control, string read, int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor("poc_overlap_store"),
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
            .Configure<PocCounterOptions>(options => options.MaintainPartitionCounter = false)
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
