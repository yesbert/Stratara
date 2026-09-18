using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Orleans.Aggregates;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.IntegrationTests.HeavyWork;

/// <summary>
/// Scenarios <em>A heavy handler computes past the grace without yielding</em> and <em>Two heavy handlers compute
/// without yielding</em>: handlers that sleep the thread for five seconds under a two-second grace and a two-second
/// permit lease. The lease and the permit are renewed from a timer of their own, not from the activation's scheduler
/// the handler is blocking, so the drain never claims the command, it runs once, and its permit is held until it
/// ends — and two such handlers run at the same time on the silo's own workers instead of one after another, so
/// neither is handed over again while the other computes.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class HeavyNonYieldingTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const string Database = "poc_heavy_non_yielding";
    private const int ComputeMs = 5_000;
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PermitLease = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_handler_that_never_yields_runs_once_and_keeps_its_permit()
    {
        var store = postgres.ConnectionStringFor(Database);
        using var app = await StartAsync(store, siloPort: 11313, gatewayPort: 30203);
        var aggregateId = Guid.NewGuid();
        var executions = app.Services.GetRequiredService<ExecutionCount>();
        var permits = app.Services.GetRequiredService<IGrainFactory>().GetGrain<IHeavyWorkPermitGrain>(0);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
            await scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>().EnqueueCommandAsync(new ComputeHeavily(aggregateId, ComputeMs));
        }

        Assert.True(await WaitUntilAsync(() => Task.FromResult(executions.Started(aggregateId) >= 1)), "the heavy handler never started");
        var lowestInUseWhileRunning = int.MaxValue;
        while (executions.Completed(aggregateId) == 0)
        {
            lowestInUseWhileRunning = Math.Min(lowestInUseWhileRunning, await permits.InUseAsync());
            await Task.Delay(250);
        }

        Assert.True(await WaitUntilAsync(async () => await OutboxCountAsync(store) == 0), "the command's record was not removed after it completed");
        await Task.Delay(Grace * 2);

        Assert.Equal(1, executions.Started(aggregateId));
        Assert.Equal(1, lowestInUseWhileRunning);
        await app.StopAsync();
    }

    [Fact]
    public async Task Two_handlers_that_never_yield_run_at_the_same_time_and_each_runs_once()
    {
        var store = postgres.ConnectionStringFor($"{Database}_pair");
        using var app = await StartAsync(store, siloPort: 11345, gatewayPort: 30235);
        var executions = app.Services.GetRequiredService<ExecutionCount>();
        var aggregates = new[] { Guid.NewGuid(), Guid.NewGuid() };

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
            foreach (var aggregateId in aggregates)
            {
                await dispatcher.EnqueueCommandAsync(new ComputeHeavily(aggregateId, ComputeMs));
            }
        }

        Assert.True(
            await WaitUntilAsync(() => Task.FromResult(aggregates.All(id => executions.Completed(id) == 1))),
            $"the two heavy handlers did not both complete: {string.Join(", ", aggregates.Select(id => $"{id} started {executions.Started(id)}, completed {executions.Completed(id)}"))}");
        Assert.True(await WaitUntilAsync(async () => await OutboxCountAsync(store) == 0), "a command's record was not removed after it completed");
        await Task.Delay(Grace * 2);

        Assert.Equal(2, executions.MostAtOnce);
        Assert.All(aggregates, id => Assert.Equal(1, executions.Started(id)));
        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(string store, int siloPort, int gatewayPort)
    {
        await Timers.PostgresTimerHostSchema.EnsureDatabaseAsync(store);
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
            .AddSingleton<ExecutionCount>()
            .AddScoped<ICommandHandler<ComputeHeavily>, ComputeHeavilyHandler>()
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<ComputeHeavily>()
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher(options => options.IntentGrace = Grace)
            .AddStrataraIntentStore<PocWriteDbContext>()
            .ConfigureStrataraHeavyWork(options => options.PermitLease = PermitLease)
            .AddStrataraSingletonWork<OutboxDrainWork>(options => options.KeepAlivePeriod = TimeSpan.FromSeconds(5))
            .Configure<OutboxDrainOptions>(options => options.PollingInterval = TimeSpan.FromMilliseconds(500));

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

    private static async Task<long> OutboxCountAsync(string store)
    {
        await using var connection = new NpgsqlConnection(store);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM outbox_entry", connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + SettleTimeout;
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

/// <summary>A heavy command whose handler computes without yielding for <see cref="ComputeMs"/>.</summary>
public sealed record ComputeHeavily(Guid AggregateId, int ComputeMs) : ICommand, IAggregateScopedCommand, IHeavyCommand;

public sealed class ExecutionCount
{
    private readonly ConcurrentDictionary<Guid, int> _started = new();
    private readonly ConcurrentDictionary<Guid, int> _completed = new();
    private int _running;
    private int _mostAtOnce;

    /// <summary>The most handlers that ran at the same time, so a test can tell parallel runs from consecutive ones.</summary>
    public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

    public void MarkStarted(Guid id)
    {
        _started.AddOrUpdate(id, 1, static (_, count) => count + 1);
        var running = Interlocked.Increment(ref _running);
        var most = Volatile.Read(ref _mostAtOnce);
        while (running > most && Interlocked.CompareExchange(ref _mostAtOnce, running, most) != most)
        {
            most = Volatile.Read(ref _mostAtOnce);
        }
    }

    public void MarkCompleted(Guid id)
    {
        _completed.AddOrUpdate(id, 1, static (_, count) => count + 1);
        Interlocked.Decrement(ref _running);
    }

    public int Started(Guid id) => _started.GetValueOrDefault(id);

    public int Completed(Guid id) => _completed.GetValueOrDefault(id);

    public IReadOnlyDictionary<Guid, int> AllStarted => _started;
}

public sealed class ComputeHeavilyHandler(ExecutionCount executions) : ICommandHandler<ComputeHeavily>
{
    public Task HandleAsync(ComputeHeavily command, CancellationToken cancellationToken)
    {
        executions.MarkStarted(command.AggregateId);
        Thread.Sleep(command.ComputeMs);
        executions.MarkCompleted(command.AggregateId);
        return Task.CompletedTask;
    }
}
