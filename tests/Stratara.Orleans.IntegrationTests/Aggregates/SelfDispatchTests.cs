using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Timers;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// A handler running in an aggregate's turn records two commands for its own aggregate through the dispatcher. The
/// second dispatch does not wait for the first command's handler — which cannot run before the dispatching turn ends
/// — so both are recorded at once, and they run after the dispatching command, in the order they were dispatched.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class SelfDispatchTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const string Database = "poc_self_dispatch";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Well below the runtime's thirty-second response timeout a dispatch that waited for itself would hit.</summary>
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Two_commands_a_handler_dispatches_to_its_own_aggregate_run_promptly_and_in_order()
    {
        var store = postgres.ConnectionStringFor(Database);
        using var app = await StartAsync(store, siloPort: 11257, gatewayPort: 30137);
        var aggregateId = Guid.NewGuid();
        var applied = app.Services.GetRequiredService<AppliedTable>();

        var started = Stopwatch.GetTimestamp();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
            await scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>().EnqueueCommandAsync(new DispatchTwiceToSelf(aggregateId));
        }

        Assert.True(await WaitUntilAsync(async () => await applied.OrderAsync(aggregateId) == "0,1,2"), $"the order was '{await applied.OrderAsync(aggregateId)}'");
        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.True(elapsed < Promptly, $"the three commands took {elapsed.TotalSeconds:F1} s; a dispatch waited for its own aggregate's turn");

        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(string store, int siloPort, int gatewayPort)
    {
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = store,
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.AddBackendServices();
        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddScoped<ICommandHandler<DispatchTwiceToSelf>, DispatchTwiceToSelfHandler>()
            .AddScoped<ICommandHandler<RecordStep>, RecordStepHandler>()
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<DispatchTwiceToSelf>()
            .AddTrustedType<RecordStep>()
            .AddSingleton(new AppliedTable(store))
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher()
            .AddStrataraIntentStore<PocWriteDbContext>();

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
        }

        await AppliedTable.EnsureSchemaAsync(store);
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

            await Task.Delay(100);
        }

        return false;
    }
}

public sealed record DispatchTwiceToSelf(Guid AggregateId) : ICommand, IAggregateScopedCommand;

public sealed record RecordStep(Guid AggregateId, int Step) : ICommand, IAggregateScopedCommand;

/// <summary>Records step 0, then dispatches steps 1 and 2 for its own aggregate before its turn ends.</summary>
public sealed class DispatchTwiceToSelfHandler(AppliedTable applied, ICommandOutboxDispatcher dispatcher) : ICommandHandler<DispatchTwiceToSelf>
{
    public async Task HandleAsync(DispatchTwiceToSelf command, CancellationToken cancellationToken)
    {
        await applied.RecordOrderAsync(command.AggregateId, 0);
        await dispatcher.EnqueueCommandAsync(new RecordStep(command.AggregateId, 1), cancellationToken);
        await dispatcher.EnqueueCommandAsync(new RecordStep(command.AggregateId, 2), cancellationToken);
    }
}

public sealed class RecordStepHandler(AppliedTable applied) : ICommandHandler<RecordStep>
{
    public Task HandleAsync(RecordStep command, CancellationToken cancellationToken) => applied.RecordOrderAsync(command.AggregateId, command.Step);
}
