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
/// The scenarios of <em>A host can wait until a projection has applied what was committed</em> on the PostgreSQL store
/// with the native reader: a host's own write is in the read model once the wait reached the mark; a write committed
/// while another transaction is open is not answered as reached until the projection could apply it, which a mark taken
/// at the held-back head would have done at once; and a wait started before a rebuild answers only once the rebuilt
/// model holds the mark's entries.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ProjectionProgressTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task A_host_waits_for_its_own_write()
    {
        var control = new RebuildProbeControl();
        using var app = await StartAsync(control, "poc_progress_own", siloPort: 11481, gatewayPort: 30481);
        var progress = app.Services.GetRequiredService<IProjectionProgress>();
        var streamId = Guid.NewGuid();

        await AppendAsync(app.Services, Guid.NewGuid(), streamId);
        var mark = await progress.MarkAsync(TestContext.Current.CancellationToken);
        var result = await progress.WaitAsync(typeof(RebuildProbeProjection), mark, Patient, TestContext.Current.CancellationToken);

        Assert.Equal(ProjectionWaitOutcome.Reached, result.Outcome);
        Assert.Equal(1, await CountAsync(app.Services, streamId));
        await app.StopAsync();
    }

    [Fact]
    public async Task A_write_committed_while_another_transaction_is_open_is_not_answered_as_reached_before_it_is_applied()
    {
        var control = new RebuildProbeControl();
        using var app = await StartAsync(control, "poc_progress_open", siloPort: 11482, gatewayPort: 30482);
        var progress = app.Services.GetRequiredService<IProjectionProgress>();
        var streamId = Guid.NewGuid();

        ProjectionWaitResult whileOpen;
        StoreMark mark;
        await using var writeScope = app.Services.CreateAsyncScope();
        await using (var context = await writeScope.ServiceProvider.GetRequiredService<IDbContextFactory<PocCommitOrderWriteDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await context.Database.SqlQueryRaw<string>("SELECT CAST(pg_current_xact_id() AS text) AS \"Value\"").SingleAsync(TestContext.Current.CancellationToken);

            await AppendAsync(app.Services, Guid.NewGuid(), streamId);
            mark = await progress.MarkAsync(TestContext.Current.CancellationToken);
            whileOpen = await progress.WaitAsync(typeof(RebuildProbeProjection), mark, Short, TestContext.Current.CancellationToken);
            Assert.Equal(0, await CountAsync(app.Services, streamId));

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        var afterwards = await progress.WaitAsync(typeof(RebuildProbeProjection), mark, Patient, TestContext.Current.CancellationToken);

        Assert.False(whileOpen.IsReached, "the wait answered reached while the reader could not yet apply the write");
        Assert.Equal(ProjectionWaitOutcome.TimedOut, whileOpen.Outcome);
        Assert.True(afterwards.IsReached);
        Assert.Equal(1, await CountAsync(app.Services, streamId));
        await app.StopAsync();
    }

    [Fact]
    public async Task A_wait_started_before_a_rebuild_answers_once_the_rebuilt_model_holds_the_marks_entries()
    {
        var control = new RebuildProbeControl { HoldTruncationNumber = 1 };
        using var app = await StartAsync(control, "poc_progress_rebuild", siloPort: 11483, gatewayPort: 30483);
        var progress = app.Services.GetRequiredService<IProjectionProgress>();
        var streams = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        var tenantId = Guid.NewGuid();
        foreach (var streamId in streams)
        {
            await AppendAsync(app.Services, tenantId, streamId);
        }

        var mark = await progress.MarkAsync(TestContext.Current.CancellationToken);
        Assert.True((await progress.WaitAsync(typeof(RebuildProbeProjection), mark, Patient, TestContext.Current.CancellationToken)).IsReached);

        var rebuild = app.Services.GetRequiredService<IProjectionRebuilder>().RebuildAsync(nameof(RebuildProbeProjection), TestContext.Current.CancellationToken);
        Assert.True(await WaitUntilAsync(() => control.Truncations == 1), "the rebuild never reached the truncation");
        var wait = progress.WaitAsync(typeof(RebuildProbeProjection), mark, Patient, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted, "the wait answered while the projection's readers were paused for the rebuild");

        control.HoldTruncation.TrySetResult();
        await rebuild;
        var result = await wait;

        Assert.True(result.IsReached);
        foreach (var streamId in streams)
        {
            Assert.Equal(1, await CountAsync(app.Services, streamId));
        }

        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(RebuildProbeControl control, string database, int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor(database + "_store"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.AddEventProjectionServices();
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(postgres.ConnectionStringFor(database + "_read"))
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

    private static async Task AppendAsync(IServiceProvider services, Guid tenantId, Guid streamId)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
        var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
        await events.CreateAsync<Counter>(streamId, new CounterCreated(streamId));
        await events.SaveChangesAsync();
    }

    private static async Task<long> CountAsync(IServiceProvider services, Guid streamId)
    {
        await using var scope = services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
        return await context.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM poc_rebuild_probe WHERE stream_id = {streamId}").SingleAsync();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Patient;
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
}
