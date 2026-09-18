using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Session;
using Stratara.Orleans.Aggregates;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// One silo with the durable-intent dispatcher, the intent store and the drain, on a PostgreSQL store of its own. A
/// test records commands through the recorder alone — the state a host that died before the hand-over leaves — and
/// watches the drain resume them; the probe handler records the tenant it ran under.
/// </summary>
internal static class RecordedIntentHost
{
    /// <summary>Builds the host and starts it; a test that records before the drain can see anything builds and starts in two steps.</summary>
    public static async Task<IHost> StartAsync(RecordedIntentSettings settings)
    {
        var host = await BuildAsync(settings);
        await StartAsync(host);
        return host;
    }

    /// <summary>
    /// Builds the host with its schema created and its outbox emptied, without starting it: nothing runs yet, so a
    /// test can record a backlog the drain then finds whole on its first pass.
    /// </summary>
    public static async Task<IHost> BuildAsync(RecordedIntentSettings settings)
    {
        await PocSilo.EnsureSchemaAsync(settings.Orleans);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = settings.Store,
            ["ConnectionStrings:rabbitmq"] = settings.Rabbit,
        });
        builder.Logging.AddProvider(settings.Probes.Logs);
        builder.Logging.AddFilter(typeof(IntentLease).FullName, LogLevel.Debug);
        builder.UseOrleans(silo => PocSilo.Configure(silo, settings.Orleans, settings.Redis, settings.SiloPort, settings.GatewayPort));
        builder.AddBackendServices();
        builder.Services
            .AddSingleton(settings.Probes)
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddScoped<ICommandHandler<TenantProbe>, TenantProbeHandler>()
            .AddScoped<ICommandHandler<FailingProbe>, FailingProbeHandler>()
            .AddScoped<ICommandHandler<ConflictingProbe>, ConflictingProbeHandler>()
            .AddScoped<ICommandHandler<OrderedProbe>, OrderedProbeHandler>()
            .AddAggregatesFromAssemblyContaining<IntentScenario>()
            .AddTrustedType<TenantProbe>()
            .AddTrustedType<FailingProbe>()
            .AddTrustedType<ConflictingProbe>()
            .AddTrustedType<OrderedProbe>()
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher(options => options.IntentGrace = TimeSpan.FromSeconds(2))
            .AddStrataraIntentStore<PocWriteDbContext>()
            .AddStrataraSingletonWork<OutboxDrainWork>(options => options.KeepAlivePeriod = TimeSpan.FromSeconds(5))
            .Configure<OutboxDrainOptions>(options =>
            {
                options.PollingInterval = settings.PollingInterval;
                options.BatchSize = settings.BatchSize;
            });
        if (settings.IntegrityMode is { } mode)
        {
            builder.Services.AddBusEnvelopeIntegrity(options =>
            {
                options.Mode = mode;
                options.SharedKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            });
        }

        settings.Services?.Invoke(builder.Services);
        var host = builder.Build();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
            await context.Database.ExecuteSqlRawAsync("DELETE FROM outbox_entry");
        }

        return host;
    }

    /// <summary>Starts a host built with <see cref="BuildAsync"/>.</summary>
    public static async Task StartAsync(IHost host)
    {
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await host.StartAsync(startTimeout.Token);
    }

    /// <summary>
    /// Records a probe command for <paramref name="tenantId"/> without handing it over, as a host that died right after
    /// the record leaves it, at the time the host's clock gives and under the aggregate and heavy flag given.
    /// </summary>
    public static async Task<Guid> RecordAsync(IHost host, Guid tenantId, Guid probeId, Guid? aggregateId = null, bool heavy = false)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var session = PocSessions.For(tenantId);
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(session);
        var intentId = Guid.CreateVersion7();
        var recordedAt = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        await scope.ServiceProvider.GetRequiredService<IntentRecorder>().RecordAsync(
            new IntentDispatch(intentId, session, aggregateId, heavy, recordedAt), new TenantProbe(Guid.NewGuid(), probeId), CancellationToken.None);
        return intentId;
    }

    public static async Task<T?> ScalarAsync<T>(string store, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(store);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    public static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
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
}

internal sealed record RecordedIntentSettings(
    string Store,
    string Orleans,
    string Redis,
    string Rabbit,
    int SiloPort,
    int GatewayPort,
    RecordedIntentProbes Probes,
    TimeSpan PollingInterval,
    int BatchSize,
    BusEnvelopeIntegrityMode? IntegrityMode = null,
    Action<IServiceCollection>? Services = null);

/// <summary>A command whose handler records the tenant it ran under.</summary>
public sealed record TenantProbe(Guid AggregateId, Guid ProbeId) : ICommand;

/// <summary>Which tenant each probe ran under, and when; which probes started; and every log entry the host wrote.</summary>
public sealed class RecordedIntentProbes
{
    public ConcurrentDictionary<Guid, (Guid Tenant, DateTimeOffset At)> Ran { get; } = new();

    /// <summary>Probes whose handler has begun, whether or not it has ended.</summary>
    public ConcurrentDictionary<Guid, DateTimeOffset> Started { get; } = new();

    /// <summary>Probes whose handler waits to be released, so a test can read the store while the command is running.</summary>
    public ConcurrentDictionary<Guid, TaskCompletionSource> Gates { get; } = new();

    public CapturedLogs Logs { get; } = new();

    /// <summary>How often each probe's handler ran, whether it completed or threw.</summary>
    public ConcurrentDictionary<Guid, int> Runs { get; } = new();

    /// <summary>The sequence numbers of the ordered probes, per aggregate, in the order their handlers ran.</summary>
    public ConcurrentDictionary<Guid, ConcurrentQueue<int>> Order { get; } = new();

    public void Counted(Guid probeId) => Runs.AddOrUpdate(probeId, 1, static (_, count) => count + 1);

    public int RunsOf(Guid probeId) => Runs.GetValueOrDefault(probeId);

    /// <summary>Holds the probe's handler until the returned source is completed.</summary>
    public TaskCompletionSource Hold(Guid probeId) =>
        Gates.GetOrAdd(probeId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}

public sealed class TenantProbeHandler(RecordedIntentProbes probes, ISessionContextProvider sessions) : ICommandHandler<TenantProbe>
{
    public async Task HandleAsync(TenantProbe command, CancellationToken cancellationToken)
    {
        probes.Started[command.ProbeId] = DateTimeOffset.UtcNow;
        probes.Counted(command.ProbeId);
        if (probes.Gates.TryGetValue(command.ProbeId, out var gate))
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        probes.Ran[command.ProbeId] = (sessions.Current?.TenantId ?? Guid.Empty, DateTimeOffset.UtcNow);
    }
}

/// <summary>A command whose handler fails on every attempt.</summary>
public sealed record FailingProbe(Guid AggregateId, Guid ProbeId) : ICommand, IAggregateScopedCommand;

/// <summary>A command whose handler meets a concurrency conflict on every attempt.</summary>
public sealed record ConflictingProbe(Guid AggregateId, Guid ProbeId) : ICommand, IAggregateScopedCommand;

/// <summary>A command whose handler records its sequence number in its aggregate's order; a test may slow its record.</summary>
public sealed record OrderedProbe(Guid AggregateId, int Sequence, bool Slow = false) : ICommand, IAggregateScopedCommand;

public sealed class FailingProbeHandler(RecordedIntentProbes probes) : ICommandHandler<FailingProbe>
{
    public Task HandleAsync(FailingProbe command, CancellationToken cancellationToken)
    {
        probes.Counted(command.ProbeId);
        throw new InvalidOperationException($"probe {command.ProbeId} fails on every attempt");
    }
}

public sealed class ConflictingProbeHandler(RecordedIntentProbes probes) : ICommandHandler<ConflictingProbe>
{
    public Task HandleAsync(ConflictingProbe command, CancellationToken cancellationToken)
    {
        probes.Counted(command.ProbeId);
        throw new ConcurrencyConflictException($"probe {command.ProbeId} conflicts on every attempt");
    }
}

public sealed class OrderedProbeHandler(RecordedIntentProbes probes) : ICommandHandler<OrderedProbe>
{
    public Task HandleAsync(OrderedProbe command, CancellationToken cancellationToken)
    {
        probes.Order.GetOrAdd(command.AggregateId, _ => new ConcurrentQueue<int>()).Enqueue(command.Sequence);
        return Task.CompletedTask;
    }
}
