using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Timers;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>
/// Scenarios <em>A command silo runs without a broker</em> and <em>A silo commits without a store-reading role</em>:
/// a silo composed with the command services composite, the execution model's dispatcher and intent store, the
/// aggregate grains and the projection role, and a client host with the dispatcher, run a command, commit its facts
/// and apply them with no broker configured and a bus that throws on every member; a silo with the command role and
/// no store-reading role still publishes its bundles to the bus.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class BrokerFreeSiloTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task A_command_silo_and_a_client_run_a_command_and_apply_its_facts_without_a_broker()
    {
        const string clusterId = "stratara-poc-broker-free";
        var log = new RoleLog();
        var store = postgres.ConnectionStringFor("poc_broker_free_store");
        var read = postgres.ConnectionStringFor("poc_broker_free_read");
        var siloBus = new ThrowingBus();
        var apiBus = new ThrowingBus();
        using var silo = await StartSiloAsync(log, store, read, clusterId, siloPort: 11334, gatewayPort: 30224, siloBus);
        using var api = await StartClientAsync(store, clusterId, apiBus);

        var aggregates = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        await using (var scope = api.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
            foreach (var aggregateId in aggregates)
            {
                await dispatcher.EnqueueCommandAsync(new RecordWhere(aggregateId), TestContext.Current.CancellationToken);
            }
        }

        Assert.True(
            await WaitUntilAsync(() => log.Count("command") == aggregates.Count && log.Count("projection") == aggregates.Count),
            $"the commands did not all run and apply: {log}");
        Assert.All(log.Entries("command"), entry => Assert.Equal("silo", entry.Silo));
        Assert.Equal(0, await BundlesInOutboxAsync(silo.Services));
        Assert.Empty(siloBus.Uses);
        Assert.Empty(apiBus.Uses);

        await api.StopAsync();
        await silo.StopAsync();
    }

    [Fact]
    public async Task A_silo_with_the_command_role_only_publishes_its_bundles_to_the_bus()
    {
        const string clusterId = "stratara-poc-broker-commands-only";
        var log = new RoleLog();
        var bus = new RecordingBus();
        var store = postgres.ConnectionStringFor("poc_broker_commands_only_store");
        using var silo = await StartSiloAsync(log, store, read: null, clusterId, siloPort: 11335, gatewayPort: 30225, bus);

        var aggregateId = Guid.NewGuid();
        await using (var scope = silo.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
            await scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>()
                .EnqueueCommandAsync(new RecordWhere(aggregateId), TestContext.Current.CancellationToken);
        }

        Assert.True(await WaitUntilAsync(() => log.Count("command") == 1 && !bus.Published.IsEmpty), $"the bundle was not published: {log}, {bus.Published.Count} publications");
        Assert.Contains(bus.Published, topic => topic.Contains("bundle", StringComparison.OrdinalIgnoreCase));

        await silo.StopAsync();
    }

    private async Task<IHost> StartSiloAsync(RoleLog log, string store, string? read, string clusterId, int siloPort, int gatewayPort, IMessageBus bus)
    {
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:defaultdb"] = store });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort, clusterId: clusterId));
        builder.AddCommandServices();
        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddSingleton(log)
            .AddSingleton(new SiloTag("silo"))
            .AddScoped<ICommandHandler<RecordWhere>, RecordWhereHandler>()
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddTrustedType<RecordWhere>()
            .Configure<PocCounterOptions>(options => options.MaintainPartitionCounter = false)
            .AddStrataraOrleansCommandDispatcher()
            .AddStrataraIntentStore<PocCommitOrderWriteDbContext>()
            .AddStrataraAggregateGrains();
        if (read is not null)
        {
            await PostgresTimerHostSchema.EnsureDatabaseAsync(read);
            builder.AddEventProjectionServices();
            builder.Services
                .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(read)
                .AddScoped<IProjection, WhereProjection>()
                .AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
                .AddStrataraProjectionCheckpoints<PocReadDbContext>()
                .AddStrataraProjectionGrains(options =>
                {
                    options.PollInterval = TimeSpan.FromSeconds(1);
                    options.KeepAlivePeriod = TimeSpan.FromSeconds(5);
                });
        }

        builder.Services.Replace(ServiceDescriptor.Singleton(bus));

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using (var write = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocCommitOrderWriteDbContext>>().CreateDbContextAsync())
            {
                await write.Database.EnsureCreatedAsync();
            }

            if (read is not null)
            {
                await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
                await context.Database.EnsureCreatedAsync();
            }
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private async Task<IHost> StartClientAsync(string store, string clusterId, IMessageBus bus)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:defaultdb"] = store });
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
            .AddTrustedType<RecordWhere>()
            .AddStrataraOrleansCommandDispatcher()
            .AddStrataraIntentStore<PocCommitOrderWriteDbContext>()
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

    /// <summary>
    /// A bus that fails on every member and records the use, so a use whose failure a caller swallows — a hosted worker
    /// that retries its subscription — still fails the test.
    /// </summary>
    private sealed class ThrowingBus : IMessageBus
    {
        public ConcurrentQueue<string> Uses { get; } = new();

        public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default) =>
            throw Used($"a publish to '{topic}'");

        public Task EnsureSubscriptionAsync(string topic, string subscription, CancellationToken cancellationToken = default) =>
            throw Used($"the subscription '{subscription}' on '{topic}' was established");

        public Task SubscribeAsync<T>(string topic, string subscription, Func<T, Task> handler, CancellationToken cancellationToken = default) =>
            throw Used($"'{subscription}' subscribed to '{topic}'");

        private InvalidOperationException Used(string use)
        {
            Uses.Enqueue(use);
            return new InvalidOperationException($"The bus was used: {use}.");
        }
    }

    /// <summary>A bus that records the topics published to.</summary>
    private sealed class RecordingBus : IMessageBus
    {
        public ConcurrentQueue<string> Published { get; } = new();

        public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
        {
            Published.Enqueue(topic);
            return Task.CompletedTask;
        }

        public Task EnsureSubscriptionAsync(string topic, string subscription, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SubscribeAsync<T>(string topic, string subscription, Func<T, Task> handler, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
