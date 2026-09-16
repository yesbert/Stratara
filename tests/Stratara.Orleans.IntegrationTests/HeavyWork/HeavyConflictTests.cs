using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.IntegrationTests.HeavyWork;

/// <summary>
/// A heavy command and a command on the same aggregate both append while the heavy command runs: the store's
/// version check refuses the heavy command's later append, and the heavy command is resumed and succeeds.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class HeavyConflictTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int HoldMs = 3_000;
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(45);

    [Fact]
    public async Task A_heavy_append_refused_by_a_concurrent_append_is_resumed_and_succeeds()
    {
        using var app = await StartAsync(siloPort: 11303, gatewayPort: 30193);
        var tenantId = Guid.NewGuid();
        var aggregateId = Guid.NewGuid();
        var log = app.Services.GetRequiredService<AppendLog>();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
            await events.CreateAsync<Counter>(aggregateId, new CounterCreated(aggregateId));
            await events.SaveChangesAsync();
        }

        await DispatchAsync(app.Services, tenantId, new AppendHeavily(aggregateId, HoldMs));
        Assert.True(await WaitUntilAsync(() => log.HeavyAttempts(aggregateId) >= 1, SettleTimeout), "the heavy command never started");
        await DispatchAsync(app.Services, tenantId, new AppendNow(aggregateId));

        Assert.True(await WaitUntilAsync(() => log.HeavySaved(aggregateId) && log.InteractiveSaved(aggregateId), SettleTimeout),
            $"not both appends landed: heavy attempts {log.HeavyAttempts(aggregateId)}, conflicts {log.Conflicts(aggregateId)}, heavy saved {log.HeavySaved(aggregateId)}, interactive saved {log.InteractiveSaved(aggregateId)}");
        Assert.Equal(1, log.Conflicts(aggregateId));
        Assert.Equal(2, log.HeavyAttempts(aggregateId));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
            Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<IEventSource>().GetCurrentVersionAsync(aggregateId));
        }

        await app.StopAsync();
    }

    private static async Task DispatchAsync<TCommand>(IServiceProvider services, Guid tenantId, TCommand command)
        where TCommand : ICommand
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
        await scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>().EnqueueCommandAsync(command);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return condition();
    }

    private async Task<IHost> StartAsync(int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor("poc_heavy_conflict_store"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.AddBackendServices();
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddSingleton<AppendLog>()
            .AddScoped<ICommandHandler<AppendHeavily>, AppendHeavilyHandler>()
            .AddScoped<ICommandHandler<AppendNow>, AppendNowHandler>()
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<AppendHeavily>()
            .AddTrustedType<AppendNow>()
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher(options => options.IntentGrace = TimeSpan.FromSeconds(2))
            .AddStrataraIntentStore<PocWriteDbContext>()
            .AddStrataraSingletonWork<OutboxDrainWork>(options => options.KeepAlivePeriod = TimeSpan.FromSeconds(5))
            .Configure<OutboxDrainOptions>(options => options.PollingInterval = TimeSpan.FromSeconds(1));

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
}

/// <summary>A heavy command that appends to its aggregate and, on its first attempt, holds before saving.</summary>
public sealed record AppendHeavily(Guid AggregateId, int HoldMs) : ICommand, IAggregateScopedCommand, IHeavyCommand;

/// <summary>A command that appends to its aggregate at once.</summary>
public sealed record AppendNow(Guid AggregateId) : ICommand, IAggregateScopedCommand;

public sealed class AppendHeavilyHandler(IEventSource events, AppendLog log) : ICommandHandler<AppendHeavily>
{
    public async Task HandleAsync(AppendHeavily command, CancellationToken cancellationToken)
    {
        var attempt = log.HeavyStarted(command.AggregateId);
        await events.AppendAsync<Counter>(command.AggregateId, new CounterIncremented(command.AggregateId, 100), cancellationToken);
        if (attempt == 1)
        {
            await Task.Delay(command.HoldMs, cancellationToken);
        }

        try
        {
            await events.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            log.Conflicted(command.AggregateId);
            throw;
        }

        log.HeavyDone(command.AggregateId);
    }
}

public sealed class AppendNowHandler(IEventSource events, AppendLog log) : ICommandHandler<AppendNow>
{
    public async Task HandleAsync(AppendNow command, CancellationToken cancellationToken)
    {
        await events.AppendAsync<Counter>(command.AggregateId, new CounterIncremented(command.AggregateId, 1), cancellationToken);
        await events.SaveChangesAsync(cancellationToken);
        log.InteractiveDone(command.AggregateId);
    }
}

/// <summary>What the two handlers did, per aggregate.</summary>
public sealed class AppendLog
{
    private readonly ConcurrentDictionary<Guid, int> _heavyAttempts = new();
    private readonly ConcurrentDictionary<Guid, int> _conflicts = new();
    private readonly ConcurrentDictionary<Guid, byte> _heavySaved = new();
    private readonly ConcurrentDictionary<Guid, byte> _interactiveSaved = new();

    public int HeavyStarted(Guid id) => _heavyAttempts.AddOrUpdate(id, 1, (_, n) => n + 1);

    public void Conflicted(Guid id) => _conflicts.AddOrUpdate(id, 1, (_, n) => n + 1);

    public void HeavyDone(Guid id) => _heavySaved[id] = 0;

    public void InteractiveDone(Guid id) => _interactiveSaved[id] = 0;

    public int HeavyAttempts(Guid id) => _heavyAttempts.GetValueOrDefault(id);

    public int Conflicts(Guid id) => _conflicts.GetValueOrDefault(id);

    public bool HeavySaved(Guid id) => _heavySaved.ContainsKey(id);

    public bool InteractiveSaved(Guid id) => _interactiveSaved.ContainsKey(id);
}
