using System.Collections.Concurrent;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Abstractions.Timers;
using Stratara.Orleans.Aggregates;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Timers;
using Stratara.Projections.Abstractions;
using Stratara.Sagas.Abstractions;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>
/// Scenarios <em>Silos register different roles</em>, <em>No silo registers a role</em> and <em>The API host joins as
/// a client</em>: one silo registers the command role, another the projection, saga and timer roles, and every
/// activation lands on the silo that registered its role; a cluster in which no silo registered the command role
/// refuses the aggregate's placement naming the role; a host with only the dispatcher joins as an Orleans client
/// and its commands run on the command silo.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class RoleSplitTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int Commands = 6;
    private const int TimerCount = 2;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan TimerDueIn = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Every_activation_runs_on_a_silo_that_registered_its_role()
    {
        const string clusterId = "stratara-poc-roles-split";
        var log = new RoleLog();
        var store = postgres.ConnectionStringFor("poc_roles_store");
        var read = postgres.ConnectionStringFor("poc_roles_read");
        using var readers = await StartReadersAsync(log, store, read, clusterId, siloPort: 11316, gatewayPort: 30206);
        using var commands = await StartCommandsAsync(log, store, clusterId, siloPort: 11315, gatewayPort: 30205);

        var aggregates = Enumerable.Range(0, Commands).Select(_ => Guid.NewGuid()).ToList();
        await using (var scope = commands.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
            foreach (var aggregateId in aggregates)
            {
                await dispatcher.EnqueueCommandAsync(new RecordWhere(aggregateId));
            }
        }

        var timers = commands.Services.GetRequiredService<IDurableTimers>();
        var owners = Enumerable.Range(0, TimerCount).Select(i => $"roles-owner-{i}-{Guid.NewGuid():N}").ToList();
        foreach (var owner in owners)
        {
            await timers.RegisterAsync(new TimerRegistration(owner, "expire", DateTimeOffset.UtcNow + TimerDueIn));
        }

        Assert.True(
            await WaitUntilAsync(() => log.Count("command") == Commands && log.Count("projection") == Commands && log.Count("saga") == Commands && log.Count("timer") == TimerCount),
            $"not every role ran: {log}");
        Assert.All(log.Entries("command"), entry => Assert.Equal("commands", entry.Silo));
        Assert.All(log.Entries("projection"), entry => Assert.Equal("readers", entry.Silo));
        Assert.All(log.Entries("saga"), entry => Assert.Equal("readers", entry.Silo));
        Assert.All(log.Entries("timer"), entry => Assert.Equal("readers", entry.Silo));

        await commands.StopAsync();
        await readers.StopAsync();
    }

    [Fact]
    public async Task A_cluster_in_which_no_silo_registered_the_command_role_refuses_the_placement_naming_it()
    {
        const string clusterId = "stratara-poc-roles-none";
        var log = new RoleLog();
        var store = postgres.ConnectionStringFor("poc_roles_none_store");
        var read = postgres.ConnectionStringFor("poc_roles_none_read");
        using var readers = await StartReadersAsync(log, store, read, clusterId, siloPort: 11317, gatewayPort: 30207);

        var grain = readers.Services.GetRequiredService<IGrainFactory>().GetGrain<IAggregateGrain>(Guid.NewGuid());
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => grain.ExecuteAsync(new AggregateCommandEnvelope("type", "{}", "{}")));

        var refusal = Flatten(failure).FirstOrDefault(ex => ex.Message.Contains("commands role", StringComparison.Ordinal));
        Assert.True(refusal is not null, $"the placement failure did not name the role: {failure}");
        Assert.Contains("AddStrataraAggregateGrains", refusal.Message, StringComparison.Ordinal);
        await readers.StopAsync();
    }

    [Fact]
    public async Task A_host_with_only_the_dispatcher_joins_as_a_client_and_its_commands_run_on_the_command_silo()
    {
        const string clusterId = "stratara-poc-roles-client";
        var log = new RoleLog();
        var store = postgres.ConnectionStringFor("poc_roles_client_store");
        using var commands = await StartCommandsAsync(log, store, clusterId, siloPort: 11318, gatewayPort: 30208);
        using var api = await StartClientAsync(log, store, clusterId);

        var aggregates = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        await using (var scope = api.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
            foreach (var aggregateId in aggregates)
            {
                await dispatcher.EnqueueCommandAsync(new RecordWhere(aggregateId));
            }
        }

        Assert.True(await WaitUntilAsync(() => log.Count("command") == aggregates.Count), $"the client's commands did not all run: {log}");
        Assert.All(log.Entries("command"), entry => Assert.Equal("commands", entry.Silo));

        await api.StopAsync();
        await commands.StopAsync();
    }

    private async Task<IHost> StartCommandsAsync(RoleLog log, string store, string clusterId, int siloPort, int gatewayPort)
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
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort, clusterId: clusterId));
        builder.AddBackendServices();
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddSingleton(log)
            .AddSingleton(new SiloTag("commands"))
            .AddScoped<ICommandHandler<RecordWhere>, RecordWhereHandler>()
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddTrustedType<RecordWhere>()
            .Configure<CommitOrderOptions>(options => options.MaintainPartitionCounter = false)
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher()
            .AddStrataraIntentStore<PocCommitOrderWriteDbContext>()
            .AddStrataraDurableTimers(options => options.RetryPeriod = TimeSpan.FromSeconds(1));

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocCommitOrderWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private async Task<IHost> StartReadersAsync(RoleLog log, string store, string read, string clusterId, int siloPort, int gatewayPort)
    {
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(read);
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = store,
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort, clusterId: clusterId));
        builder.AddEventProjectionServices();
        builder.AddSagaServices();
        var timerHost = new TaggedTimerHost(log, new SiloTag("readers"));
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(read)
            .AddSingleton(log)
            .AddSingleton(new SiloTag("readers"))
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddScoped<IProjection, WhereProjection>()
            .AddScoped<ISaga, WhereSaga>()
            .Configure<CommitOrderOptions>(options => options.MaintainPartitionCounter = false)
            .AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
            .AddStrataraProjectionCheckpoints<PocReadDbContext>()
            .AddStrataraProjectionGrains(options =>
            {
                options.PollInterval = TimeSpan.FromSeconds(1);
                options.KeepAlivePeriod = TimeSpan.FromSeconds(5);
            })
            .AddStrataraSagaGrains(options =>
            {
                options.PollInterval = TimeSpan.FromSeconds(1);
                options.KeepAlivePeriod = TimeSpan.FromSeconds(5);
            })
            .AddStrataraDurableTimers(options => options.RetryPeriod = TimeSpan.FromSeconds(1))
            .AddSingleton<ITimerOwners>(timerHost)
            .AddSingleton<ITimerHandler>(timerHost);

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using (var write = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocCommitOrderWriteDbContext>>().CreateDbContextAsync())
            {
                await write.Database.EnsureCreatedAsync();
            }

            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private async Task<IHost> StartClientAsync(RoleLog log, string store, string clusterId)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = store,
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleansClient(client =>
        {
            client.Configure<ClusterOptions>(options =>
            {
                options.ClusterId = clusterId;
                options.ServiceId = clusterId;
            });
            client.UseAdoNetClustering(options =>
            {
                options.Invariant = PocSilo.AdoNetInvariant;
                options.ConnectionString = orleansConnectionString;
            });
        });
        builder.AddBackendServices();
        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddSingleton(log)
            .AddSingleton(new SiloTag("api"))
            .AddTrustedType<RecordWhere>()
            .AddStrataraOrleansCommandDispatcher()
            .AddStrataraIntentStore<PocCommitOrderWriteDbContext>();

        var app = builder.Build();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
                {
                    yield return inner;
                }
            }
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }
}

/// <summary>Which silo of the test a service runs on.</summary>
public sealed record SiloTag(string Name);

/// <summary>What ran where: one entry per role activation, with the silo it ran on.</summary>
public sealed class RoleLog
{
    private readonly ConcurrentQueue<(string Kind, string Id, string Silo)> _entries = new();

    public void Record(string kind, string id, string silo) => _entries.Enqueue((kind, id, silo));

    public int Count(string kind) => _entries.Count(entry => entry.Kind == kind);

    public IReadOnlyList<(string Kind, string Id, string Silo)> Entries(string kind) => [.. _entries.Where(entry => entry.Kind == kind)];

    public override string ToString() => string.Join(", ", _entries.GroupBy(e => (e.Kind, e.Silo)).Select(g => $"{g.Key.Kind}@{g.Key.Silo}={g.Count()}"));
}

/// <summary>A command whose handler appends a fact and records which silo ran it.</summary>
public sealed record RecordWhere(Guid AggregateId) : ICommand, IAggregateScopedCommand;

public sealed class RecordWhereHandler(IEventSource events, RoleLog log, SiloTag tag) : ICommandHandler<RecordWhere>
{
    public async Task HandleAsync(RecordWhere command, CancellationToken cancellationToken)
    {
        await events.CreateAsync<Counter>(command.AggregateId, new CounterCreated(command.AggregateId), cancellationToken);
        await events.SaveChangesAsync(cancellationToken);
        log.Record("command", command.AggregateId.ToString(), tag.Name);
    }
}

public sealed class WhereProjection(RoleLog log, SiloTag tag) : IProjection
{
    [UsedImplicitly]
    public Task HandleAsync(IEvent<CounterCreated> @event, CancellationToken cancellationToken)
    {
        log.Record("projection", @event.StreamId.ToString(), tag.Name);
        return Task.CompletedTask;
    }
}

public sealed class WhereSaga(RoleLog log, SiloTag tag) : ISaga
{
    [UsedImplicitly]
    public Task HandleAsync(CounterCreated @event, CancellationToken cancellationToken)
    {
        log.Record("saga", @event.CounterId.ToString(), tag.Name);
        return Task.CompletedTask;
    }
}

public sealed class TaggedTimerHost(RoleLog log, SiloTag tag) : ITimerOwners, ITimerHandler
{
    public Task<bool> ExistsAsync(string ownerId, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task OnDueAsync(TimerDue due, CancellationToken cancellationToken)
    {
        log.Record("timer", due.OwnerId, tag.Name);
        return Task.CompletedTask;
    }
}
