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
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Abstractions.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;

namespace Stratara.Orleans.IntegrationTests.Sagas;

/// <summary>
/// Task 9.1, first half: a saga written to the shipped contract runs unchanged in the saga grain,
/// sees every fact in commit order within a stream, and sees it under the recorded session.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class SagaGrainTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task An_unchanged_saga_sees_the_facts_in_order_under_the_recorded_session()
    {
        var log = new SagaLog();
        using var app = await StartAsync(log, siloPort: 11211, gatewayPort: 30100);
        var tenantId = Guid.NewGuid();
        var streamId = Guid.NewGuid();

        await AppendAsync(app.Services, tenantId, streamId, new CounterCreated(streamId), create: true);
        await AppendAsync(app.Services, tenantId, streamId, new CounterIncremented(streamId, 1), create: false);
        await AppendAsync(app.Services, tenantId, streamId, new CounterIncremented(streamId, 2), create: false);

        var seen = await WaitForAsync(() => log.Sequence(streamId), sequence => sequence.Count == 3);

        Assert.Equal(["created", "incremented:1", "incremented:2"], seen.Select(s => s.Step));
        Assert.All(seen, s => Assert.Equal(tenantId, s.Tenant));
        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(SagaLog log, int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor("poc_saga_store"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.AddSagaServices();
        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(postgres.ConnectionStringFor("poc_saga_read"))
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddTrustedType<CounterIncremented>()
            .AddScoped<Stratara.Sagas.Abstractions.ISaga, CounterSaga>()
            .AddSingleton(log)
            .Configure<PocCounterOptions>(options => options.MaintainPartitionCounter = false)
            .AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
            .AddStrataraProjectionCheckpoints<PocReadDbContext>()
            .AddStrataraSagaGrains(options =>
            {
                options.PollInterval = TimeSpan.FromSeconds(2);
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

    private static async Task<T> WaitForAsync<T>(Func<T> read, Func<T, bool> ready)
    {
        var deadline = DateTimeOffset.UtcNow + ApplyTimeout;
        var last = read();
        while (DateTimeOffset.UtcNow < deadline && !ready(last))
        {
            await Task.Delay(200);
            last = read();
        }

        return last;
    }
}
