using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Timers;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// Scenario <em>An aggregate's order takes longer than the response timeout</em>: five recorded commands of one
/// second each on one aggregate, under a two-second response timeout. The grain runs its order through a call to
/// itself; a call that carried a response timed out at two seconds and the grain took the fault for "the run never
/// started", failing the three commands still waiting and dropping their leases. Every command runs, in order.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class LongOrderTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const string Database = "poc_long_order";
    private const int Commands = 5;
    private const int HoldMs = 1_000;
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task An_order_longer_than_the_response_timeout_runs_every_command_in_order()
    {
        var log = new TurnLog();
        using var app = await StartAsync(log, siloPort: 11311, gatewayPort: 30201);
        var aggregateId = Guid.NewGuid();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
            for (var i = 0; i < Commands; i++)
            {
                await dispatcher.EnqueueCommandAsync(new HoldOrder(aggregateId, i, HoldMs));
            }
        }

        var expected = Enumerable.Range(0, Commands).SelectMany(i => new[] { $"hold:{i}:start", $"hold:{i}:end" }).ToList();
        Assert.True(await WaitUntilAsync(() => log.Entries(aggregateId).Count >= expected.Count), $"only {log.Entries(aggregateId).Count} of {expected.Count} steps ran within {SettleTimeout}: {string.Join(", ", log.Entries(aggregateId))}");
        Assert.Equal(expected, log.Entries(aggregateId));
        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(TurnLog log, int siloPort, int gatewayPort)
    {
        var store = postgres.ConnectionStringFor(Database);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = store,
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort)
            .Configure<SiloMessagingOptions>(options => options.ResponseTimeout = ResponseTimeout));
        builder.AddBackendServices();
        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddScoped<ICommandHandler<HoldOrder>, HoldOrderHandler>()
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<HoldOrder>()
            .AddSingleton(log)
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher()
            .AddStrataraIntentStore<PocWriteDbContext>();

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
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
}

public sealed record HoldOrder(Guid AggregateId, int Index, int DelayMs) : ICommand, IAggregateScopedCommand;

public sealed class HoldOrderHandler(TurnLog log) : ICommandHandler<HoldOrder>
{
    public async Task HandleAsync(HoldOrder command, CancellationToken cancellationToken)
    {
        log.Record(command.AggregateId, $"hold:{command.Index}:start");
        await Task.Delay(command.DelayMs, cancellationToken);
        log.Record(command.AggregateId, $"hold:{command.Index}:end");
    }
}
