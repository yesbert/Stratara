using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Sagas.Abstractions;
using Stratara.Diagnostics;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Timers;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>
/// Scenarios <em>A client host commits facts in process</em>, <em>A command silo without a store-reading role registers
/// the store wake-ups</em>, <em>A silo with one store-reading role registers the store wake-ups</em> and <em>The cluster
/// hosts no saga role</em>: the readers poll every two minutes, so a fact applied within seconds was applied because a
/// wake-up reached it; no host has a broker, and a bus that throws on every member stands in for one.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ClientWakeUpTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private static readonly TimeSpan SlowPoll = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Woken = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_client_that_commits_in_process_wakes_the_readers_and_stores_nothing()
    {
        var cluster = $"stratara-poc-wake-client-{Guid.NewGuid():N}";
        var (store, read) = Databases("client");
        var log = new RoleLog();
        using var readers = await StartSiloAsync(log, store, read, cluster, 11500, 30500, Roles.Projections | Roles.Sagas);
        var bus = new ThrowingBus();
        var logs = new CapturedLogs();
        using var client = await StartClientAsync(log, store, cluster, bus, logs);

        var counter = Guid.NewGuid();
        await CommitOnClientAsync(client, counter);

        Assert.True(await WaitUntilAsync(() => log.Count("projection") == 1 && log.Count("saga") == 1, Woken), $"the readers were not woken: {log}");
        Assert.Equal(0, await BundlesInOutboxAsync(readers.Services));
        Assert.Empty(bus.Uses);
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("Will store in outbox", StringComparison.Ordinal));

        await client.StopAsync();
        await readers.StopAsync();
    }

    [Fact]
    public async Task A_command_silo_without_a_reader_wakes_the_readers_on_another_silo()
    {
        var cluster = $"stratara-poc-wake-commands-{Guid.NewGuid():N}";
        var (store, read) = Databases("commands");
        var log = new RoleLog();
        using var readers = await StartSiloAsync(log, store, read, cluster, 11501, 30501, Roles.Projections | Roles.Sagas);
        var bus = new ThrowingBus();
        using var commands = await StartSiloAsync(log, store, read: null, cluster, 11502, 30502, Roles.Commands | Roles.WakeUps, bus);

        await DispatchAsync(commands, Guid.NewGuid());

        Assert.True(await WaitUntilAsync(() => log.Count("command") == 1 && log.Count("projection") == 1 && log.Count("saga") == 1, Woken), $"the readers were not woken: {log}");
        Assert.Equal(0, await BundlesInOutboxAsync(readers.Services));
        Assert.Empty(bus.Uses);

        await commands.StopAsync();
        await readers.StopAsync();
    }

    [Fact]
    public async Task A_silo_with_the_saga_role_wakes_its_own_sagas_and_the_projections_elsewhere()
    {
        var cluster = $"stratara-poc-wake-mixed-{Guid.NewGuid():N}";
        var (store, read) = Databases("mixed");
        var log = new RoleLog();
        using var projections = await StartSiloAsync(log, store, read, cluster, 11503, 30503, Roles.Projections);
        var bus = new ThrowingBus();
        using var commands = await StartSiloAsync(log, store, read, cluster, 11504, 30504, Roles.Commands | Roles.Sagas | Roles.WakeUps, bus);

        await DispatchAsync(commands, Guid.NewGuid());

        Assert.True(await WaitUntilAsync(() => log.Count("projection") == 1 && log.Count("saga") == 1, Woken), $"both roles were not woken: {log}");
        Assert.Empty(bus.Uses);

        await commands.StopAsync();
        await projections.StopAsync();
    }

    [Fact]
    public async Task A_role_no_silo_hosts_is_set_aside_and_woken_again_once_a_silo_of_it_joins()
    {
        var cluster = $"stratara-poc-wake-absent-{Guid.NewGuid():N}";
        var (store, read) = Databases("absent");
        var log = new RoleLog();
        using var projections = await StartSiloAsync(log, store, read, cluster, 11505, 30505, Roles.Projections);
        var logs = new CapturedLogs();
        using var client = await StartClientAsync(log, store, cluster, new ThrowingBus(), logs, absentRoleRetry: TimeSpan.FromSeconds(2));

        for (var commit = 0; commit < 5; commit++)
        {
            await CommitOnClientAsync(client, Guid.NewGuid());
        }

        Assert.True(await WaitUntilAsync(() => log.Count("projection") == 5, Woken), $"the projections were not woken: {log}");
        Assert.Single(logs.Entries, entry => entry.EventId == LogEvents.Orleans.StoreWakeUpRoleAbsent);

        using var sagas = await StartSiloAsync(log, store, read, cluster, 11506, 30506, Roles.Sagas);
        Assert.True(await WaitUntilAsync(() => log.Count("saga") == 5, Woken), $"the saga silo did not catch up at its start: {log}");
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await CommitOnClientAsync(client, Guid.NewGuid());

        Assert.True(await WaitUntilAsync(() => log.Count("saga") == 6, Woken), $"the saga silo that joined was not woken: {log}");
        Assert.Contains(logs.Entries, entry => entry.EventId == LogEvents.Orleans.StoreWakeUpRoleBack);

        await client.StopAsync();
        await sagas.StopAsync();
        await projections.StopAsync();
    }

    private (string Store, string Read) Databases(string name) =>
        (postgres.ConnectionStringFor($"poc_wake_{name}_store"), postgres.ConnectionStringFor($"poc_wake_{name}_read"));

    private static async Task CommitOnClientAsync(IHost client, Guid counter)
    {
        await using var scope = client.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
        await scope.ServiceProvider.GetRequiredService<IMediator>().HandleAsync(new CommitInProcess(counter), TestContext.Current.CancellationToken);
    }

    private static async Task DispatchAsync(IHost host, Guid aggregateId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
        await scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>().EnqueueCommandAsync(new RecordWhere(aggregateId), TestContext.Current.CancellationToken);
    }

    private async Task<IHost> StartSiloAsync(RoleLog log, string store, string? read, string clusterId, int siloPort, int gatewayPort, Roles roles, IMessageBus? bus = null)
    {
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        var orleans = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleans);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:defaultdb"] = store });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleans, redis.ConnectionString, siloPort, gatewayPort, clusterId: clusterId));
        if (roles.HasFlag(Roles.Commands))
        {
            builder.AddCommandServices();
        }
        else
        {
            builder.AddBackendServices();
        }

        if (roles.HasFlag(Roles.Projections))
        {
            builder.AddEventProjectionServices();
        }

        if (roles.HasFlag(Roles.Sagas))
        {
            builder.AddSagaServices();
        }

        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddSingleton(log)
            .AddSingleton(new SiloTag($"silo-{siloPort}"))
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddTrustedType<RecordWhere>()
            .Configure<PocCounterOptions>(options => options.MaintainPartitionCounter = false)
            .Replace(ServiceDescriptor.Singleton(bus ?? new ThrowingBus()));
        if (roles.HasFlag(Roles.Commands))
        {
            builder.Services
                .AddScoped<ICommandHandler<RecordWhere>, RecordWhereHandler>()
                .AddStrataraOrleansCommandDispatcher()
                .AddStrataraIntentStore<PocCommitOrderWriteDbContext>()
                .AddStrataraAggregateGrains();
        }

        if (read is not null && (roles.HasFlag(Roles.Projections) || roles.HasFlag(Roles.Sagas)))
        {
            await PostgresTimerHostSchema.EnsureDatabaseAsync(read);
            builder.Services
                .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(read)
                .AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
                .AddStrataraProjectionCheckpoints<PocReadDbContext>();
        }

        if (roles.HasFlag(Roles.Projections))
        {
            builder.Services
                .AddScoped<IProjection, WhereProjection>()
                .AddStrataraProjectionGrains(options =>
                {
                    options.PollInterval = SlowPoll;
                    options.KeepAlivePeriod = TimeSpan.FromSeconds(5);
                });
        }

        if (roles.HasFlag(Roles.Sagas))
        {
            builder.Services
                .AddScoped<ISaga, WhereSaga>()
                .AddStrataraSagaGrains(options =>
                {
                    options.PollInterval = SlowPoll;
                    options.KeepAlivePeriod = TimeSpan.FromSeconds(5);
                });
        }

        if (roles.HasFlag(Roles.WakeUps))
        {
            builder.Services.AddStrataraStoreWakeUps();
        }

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using (var write = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocCommitOrderWriteDbContext>>().CreateDbContextAsync())
            {
                await write.Database.EnsureCreatedAsync();
            }

            if (read is not null && scope.ServiceProvider.GetService<IDbContextFactory<PocReadDbContext>>() is { } readContexts)
            {
                await using var context = await readContexts.CreateDbContextAsync();
                await context.Database.EnsureCreatedAsync();
            }
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private async Task<IHost> StartClientAsync(RoleLog log, string store, string clusterId, IMessageBus bus, CapturedLogs logs, TimeSpan? absentRoleRetry = null)
    {
        var orleans = postgres.ConnectionStringFor("poc_orleans");

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:defaultdb"] = store });
        builder.Logging.AddProvider(logs);
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
                options.ConnectionString = orleans;
            });
        });
        builder.AddBackendServices();
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddSingleton(log)
            .AddSingleton(new SiloTag("client"))
            .AddScoped<ICommandHandler<CommitInProcess>, CommitInProcessHandler>()
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .Configure<PocCounterOptions>(options => options.MaintainPartitionCounter = false)
            .AddStrataraOrleansCommandDispatcher()
            .AddStrataraIntentStore<PocCommitOrderWriteDbContext>()
            .AddStrataraStoreWakeUps(options => options.AbsentRoleRetry = absentRoleRetry ?? TimeSpan.FromMinutes(1))
            .Replace(ServiceDescriptor.Singleton(bus));

        var app = builder.Build();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static async Task<int> BundlesInOutboxAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocCommitOrderWriteDbContext>>().CreateDbContextAsync();
        return await context.Set<OutboxEntry>().CountAsync(e => e.DataTypeName.Contains("EventBundle"), TestContext.Current.CancellationToken);
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

        return false;
    }

    [Flags]
    private enum Roles
    {
        Commands = 1,
        Projections = 2,
        Sagas = 4,
        WakeUps = 8,
    }

    /// <summary>A bus that records every use and throws, so a host that reaches for a broker is seen to.</summary>
    private sealed class ThrowingBus : IMessageBus
    {
        public ConcurrentQueue<string> Uses { get; } = new();

        public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default) => Use($"publish {topic}");

        public Task EnsureSubscriptionAsync(string topic, string subscription, CancellationToken cancellationToken = default) => Use($"ensure {topic}");

        public Task SubscribeAsync<T>(string topic, string subscription, Func<T, Task> handler, CancellationToken cancellationToken = default) => Use($"subscribe {topic}");

        private Task Use(string what)
        {
            Uses.Enqueue(what);
            return Task.FromException(new InvalidOperationException($"No broker: {what}."));
        }
    }
}

/// <summary>A command a client host runs in process: it creates a counter there and commits.</summary>
public sealed record CommitInProcess(Guid CounterId) : ICommand;

public sealed class CommitInProcessHandler(IEventSource events, RoleLog log) : ICommandHandler<CommitInProcess>
{
    public async Task HandleAsync(CommitInProcess command, CancellationToken cancellationToken)
    {
        await events.CreateAsync<Counter>(command.CounterId, new CounterCreated(command.CounterId), cancellationToken);
        await events.SaveChangesAsync(cancellationToken);
        log.Record("client-commit", command.CounterId.ToString(), "client");
    }
}
