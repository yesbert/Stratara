using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Orleans.Aggregates;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Timers;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// Scenarios <em>A heavy command runs longer than the response timeout</em>, <em>A command that names no aggregate runs
/// longer than the response timeout</em> and <em>A resumed long command</em>: under a two-second response timeout on
/// the silo and on a client host, commands whose handlers take five seconds are dispatched from the client — or
/// recorded and left to the drain — and run once to their end, while neither host logs a response that did not arrive
/// on time or an expired message the silo dropped.
/// </summary>
/// <remarks>
/// The absence of the runtime's warnings is read from their wording, which Orleans may change; the outcome — one run,
/// a committed append, a completed record — is asserted beside it, so a reworded warning can make the log check pass
/// without meaning, never the behaviour.
/// </remarks>
[Collection(InfrastructureCollection.Name)]
public sealed class LongHandOverTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int HandlerMs = 5_000;
    private const string Database = "poc_long_hand_over";
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(45);
    private static readonly string[] RuntimeTimeoutWarnings = ["Response did not arrive on time", "Dropping expired message"];

    [Fact]
    public async Task Long_heavy_and_aggregate_less_commands_dispatched_from_a_client_run_once_and_leave_no_timeout_in_either_log()
    {
        var cluster = $"stratara-poc-long-hand-over-{Guid.NewGuid():N}";
        var store = postgres.ConnectionStringFor(Database);
        var log = new LongRunLog();
        var siloLogs = new RuntimeLogs();
        var clientLogs = new RuntimeLogs();
        using var silo = await StartSiloAsync(log, siloLogs, store, cluster, siloPort: 11490, gatewayPort: 30490);
        using var client = await StartClientAsync(clientLogs, store, cluster);
        var tenantId = Guid.NewGuid();
        var aggregateId = await CreateCounterAsync(silo, tenantId);
        var heavyProbe = Guid.NewGuid();
        var unscopedProbe = Guid.NewGuid();

        await using (var scope = client.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
            await dispatcher.EnqueueCommandAsync(new SlowHeavyAppend(aggregateId, heavyProbe, HandlerMs), TestContext.Current.CancellationToken);
            await dispatcher.EnqueueCommandAsync(new SlowUnscopedProbe(unscopedProbe, HandlerMs), TestContext.Current.CancellationToken);
        }

        Assert.True(await WaitUntilAsync(() => Task.FromResult(log.Completed(heavyProbe) == 1 && log.Completed(unscopedProbe) == 1)), "the long commands did not run to their end");
        Assert.True(await WaitUntilAsync(async () => await OutboxCountAsync(store) == 0), "the long commands' records were not completed");
        await Task.Delay(ResponseTimeout * 3, TestContext.Current.CancellationToken);

        Assert.Equal(1, log.Started(heavyProbe));
        Assert.Equal(1, log.Started(unscopedProbe));
        Assert.Equal(2, await VersionAsync(silo, tenantId, aggregateId));
        Assert.Empty(siloLogs.Matching(RuntimeTimeoutWarnings));
        Assert.Empty(clientLogs.Matching(RuntimeTimeoutWarnings));

        await client.StopAsync();
        await silo.StopAsync();
    }

    [Fact]
    public async Task Resumed_long_commands_run_once_and_leave_no_timeout_in_the_log()
    {
        var cluster = $"stratara-poc-long-resume-{Guid.NewGuid():N}";
        var store = postgres.ConnectionStringFor($"{Database}_resume");
        var log = new LongRunLog();
        var siloLogs = new RuntimeLogs();
        using var silo = await StartSiloAsync(log, siloLogs, store, cluster, siloPort: 11491, gatewayPort: 30491);
        var tenantId = Guid.NewGuid();
        var aggregateId = await CreateCounterAsync(silo, tenantId);
        var heavyProbe = Guid.NewGuid();
        var unscopedProbe = Guid.NewGuid();

        await RecordAsync(silo, tenantId, new SlowHeavyAppend(aggregateId, heavyProbe, HandlerMs), aggregateId, heavy: true);
        await RecordAsync(silo, tenantId, new SlowUnscopedProbe(unscopedProbe, HandlerMs), aggregateId: null, heavy: false);

        Assert.True(await WaitUntilAsync(() => Task.FromResult(log.Completed(heavyProbe) == 1 && log.Completed(unscopedProbe) == 1)), "the resumed long commands did not run to their end");
        Assert.True(await WaitUntilAsync(async () => await OutboxCountAsync(store) == 0), "the resumed long commands' records were not completed");
        await Task.Delay(ResponseTimeout * 3, TestContext.Current.CancellationToken);

        Assert.Equal(1, log.Started(heavyProbe));
        Assert.Equal(1, log.Started(unscopedProbe));
        Assert.Equal(2, await VersionAsync(silo, tenantId, aggregateId));
        Assert.Empty(siloLogs.Matching(RuntimeTimeoutWarnings));

        await silo.StopAsync();
    }

    private static async Task<Guid> CreateCounterAsync(IHost silo, Guid tenantId)
    {
        var aggregateId = Guid.NewGuid();
        await using var scope = silo.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
        var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
        await events.CreateAsync<Counter>(aggregateId, new CounterCreated(aggregateId));
        await events.SaveChangesAsync();
        return aggregateId;
    }

    private static async Task<long> VersionAsync(IHost silo, Guid tenantId, Guid aggregateId)
    {
        await using var scope = silo.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
        return await scope.ServiceProvider.GetRequiredService<IEventSource>().GetCurrentVersionAsync(aggregateId);
    }

    private static async Task RecordAsync<TCommand>(IHost silo, Guid tenantId, TCommand command, Guid? aggregateId, bool heavy) where TCommand : ICommand
    {
        await using var scope = silo.Services.CreateAsyncScope();
        var session = PocSessions.For(tenantId);
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(session);
        var recordedAt = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        await scope.ServiceProvider.GetRequiredService<IntentRecorder>().RecordAsync(
            new IntentDispatch(Guid.CreateVersion7(), session, aggregateId, heavy, recordedAt), command, CancellationToken.None);
    }

    private static Task<long> OutboxCountAsync(string store) =>
        RecordedIntentHost.ScalarAsync<long>(store, "SELECT count(*) FROM outbox_entry");

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + SettleTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    private async Task<IHost> StartSiloAsync(LongRunLog log, RuntimeLogs logs, string store, string clusterId, int siloPort, int gatewayPort)
    {
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        var orleans = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleans);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = store,
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.Logging.AddProvider(logs);
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleans, redis.ConnectionString, siloPort, gatewayPort, clusterId: clusterId)
            .Configure<SiloMessagingOptions>(options => options.ResponseTimeout = ResponseTimeout));
        builder.AddBackendServices();
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddSingleton(log)
            .AddScoped<ICommandHandler<SlowHeavyAppend>, SlowHeavyAppendHandler>()
            .AddScoped<ICommandHandler<SlowUnscopedProbe>, SlowUnscopedProbeHandler>()
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<SlowHeavyAppend>()
            .AddTrustedType<SlowUnscopedProbe>()
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher(options => options.IntentGrace = Grace)
            .AddStrataraIntentStore<PocWriteDbContext>()
            .AddStrataraSingletonWork<OutboxDrainWork>(options => options.KeepAlivePeriod = TimeSpan.FromSeconds(5))
            .Configure<OutboxDrainOptions>(options => options.PollingInterval = TimeSpan.FromMilliseconds(500))
            .Replace(ServiceDescriptor.Singleton<IMessageBus>(new AcceptingBus()));

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
            await context.Database.ExecuteSqlRawAsync("DELETE FROM outbox_entry");
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private async Task<IHost> StartClientAsync(RuntimeLogs logs, string store, string clusterId)
    {
        var orleans = postgres.ConnectionStringFor("poc_orleans");

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = store,
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
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
            client.Configure<ClientMessagingOptions>(options => options.ResponseTimeout = ResponseTimeout);
        });
        builder.AddBackendServices();
        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddTrustedType<SlowHeavyAppend>()
            .AddTrustedType<SlowUnscopedProbe>()
            .AddStrataraOrleansCommandDispatcher(options => options.IntentGrace = Grace)
            .AddStrataraIntentStore<PocWriteDbContext>()
            .Replace(ServiceDescriptor.Singleton<IMessageBus>(new AcceptingBus()));

        var app = builder.Build();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    /// <summary>A bus that takes every bundle, so a commit is not held up by a broker that routes it nowhere.</summary>
    private sealed class AcceptingBus : IMessageBus
    {
        public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnsureSubscriptionAsync(string topic, string subscription, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SubscribeAsync<T>(string topic, string subscription, Func<T, Task> handler, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

/// <summary>A heavy command whose handler waits, then appends to its counter.</summary>
public sealed record SlowHeavyAppend(Guid AggregateId, Guid ProbeId, int DelayMs) : ICommand, IAggregateScopedCommand, IHeavyCommand;

/// <summary>A command that names no aggregate and whose handler waits.</summary>
public sealed record SlowUnscopedProbe(Guid ProbeId, int DelayMs) : ICommand;

public sealed class SlowHeavyAppendHandler(IEventSource events, LongRunLog log) : ICommandHandler<SlowHeavyAppend>
{
    public async Task HandleAsync(SlowHeavyAppend command, CancellationToken cancellationToken)
    {
        log.Start(command.ProbeId);
        await Task.Delay(command.DelayMs, cancellationToken);
        await events.AppendAsync<Counter>(command.AggregateId, new CounterIncremented(command.AggregateId, 1), cancellationToken);
        await events.SaveChangesAsync(cancellationToken);
        log.Complete(command.ProbeId);
    }
}

public sealed class SlowUnscopedProbeHandler(LongRunLog log) : ICommandHandler<SlowUnscopedProbe>
{
    public async Task HandleAsync(SlowUnscopedProbe command, CancellationToken cancellationToken)
    {
        log.Start(command.ProbeId);
        await Task.Delay(command.DelayMs, cancellationToken);
        log.Complete(command.ProbeId);
    }
}

public sealed class LongRunLog
{
    private readonly ConcurrentDictionary<Guid, int> _started = new();
    private readonly ConcurrentDictionary<Guid, int> _completed = new();

    public void Start(Guid probeId) => _started.AddOrUpdate(probeId, 1, static (_, count) => count + 1);

    public void Complete(Guid probeId) => _completed.AddOrUpdate(probeId, 1, static (_, count) => count + 1);

    public int Started(Guid probeId) => _started.GetValueOrDefault(probeId);

    public int Completed(Guid probeId) => _completed.GetValueOrDefault(probeId);
}

/// <summary>Every message a host logs at warning or above, with its category, so a test can look for the runtime's own.</summary>
public sealed class RuntimeLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyList<string> Matching(IEnumerable<string> fragments) =>
        [.. _entries.Where(entry => fragments.Any(fragment => entry.Contains(fragment, StringComparison.Ordinal)))];

    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Capture(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                entries.Enqueue($"{category}: {formatter(state, exception)}");
            }
        }
    }
}
