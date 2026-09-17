using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Session;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Timers;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// Scenario <em>A forwarded command's handler outlasts the response timeout</em>: under a two-second response timeout a
/// command forwarded into its aggregate's activation with a four-second handler fails its caller with a timeout, while
/// the handler runs once to its end and its append commits.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ForwardedTimeoutTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int HandlerMs = 4_000;
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task The_caller_times_out_while_the_handler_runs_once_and_commits()
    {
        var log = new SlowAppendLog();
        using var app = await StartAsync(log, siloPort: 11344, gatewayPort: 30234);
        var tenantId = Guid.NewGuid();
        var aggregateId = Guid.NewGuid();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
            await events.CreateAsync<Counter>(aggregateId, new CounterCreated(aggregateId));
            await events.SaveChangesAsync();
        }

        Exception? failure;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
            failure = await Record.ExceptionAsync(() => scope.ServiceProvider.GetRequiredService<IMediator>().HandleAsync(new AppendSlowly(aggregateId, HandlerMs)));
        }

        Assert.True(IsTimeout(failure), $"the caller did not observe a timeout: {failure}");
        Assert.True(await WaitUntilAsync(() => log.Completed(aggregateId) == 1), "the handler did not run to its end after the caller's timeout");
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(1, log.Started(aggregateId));
        Assert.Equal(1, log.Completed(aggregateId));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
            Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<IEventSource>().GetCurrentVersionAsync(aggregateId));
        }

        await app.StopAsync();
    }

    private static bool IsTimeout(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                return true;
            }
        }

        return false;
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

    private async Task<IHost> StartAsync(SlowAppendLog log, int siloPort, int gatewayPort)
    {
        var store = postgres.ConnectionStringFor("poc_forwarded_timeout");
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
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddScoped<ICommandHandler<AppendSlowly>, AppendSlowlyHandler>()
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<AppendSlowly>()
            .AddSingleton(log)
            .AddStrataraAggregateGrains();

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

public sealed record AppendSlowly(Guid AggregateId, int DelayMs) : ICommand, IAggregateScopedCommand;

public sealed class AppendSlowlyHandler(IEventSource events, SlowAppendLog log) : ICommandHandler<AppendSlowly>
{
    public async Task HandleAsync(AppendSlowly command, CancellationToken cancellationToken)
    {
        log.Start(command.AggregateId);
        await Task.Delay(command.DelayMs, cancellationToken);
        await events.AppendAsync<Counter>(command.AggregateId, new CounterIncremented(command.AggregateId, 1), cancellationToken);
        await events.SaveChangesAsync(cancellationToken);
        log.Complete(command.AggregateId);
    }
}

public sealed class SlowAppendLog
{
    private readonly ConcurrentDictionary<Guid, int> _started = new();
    private readonly ConcurrentDictionary<Guid, int> _completed = new();

    public void Start(Guid aggregateId) => _started.AddOrUpdate(aggregateId, 1, (_, count) => count + 1);

    public void Complete(Guid aggregateId) => _completed.AddOrUpdate(aggregateId, 1, (_, count) => count + 1);

    public int Started(Guid aggregateId) => _started.GetValueOrDefault(aggregateId);

    public int Completed(Guid aggregateId) => _completed.GetValueOrDefault(aggregateId);
}
