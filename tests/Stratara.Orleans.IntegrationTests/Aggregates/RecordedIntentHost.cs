using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
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
    public static async Task<IHost> StartAsync(RecordedIntentSettings settings)
    {
        await PocSilo.EnsureSchemaAsync(settings.Orleans);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = settings.Store,
            ["ConnectionStrings:rabbitmq"] = settings.Rabbit,
        });
        builder.Logging.AddProvider(settings.Probes.Logs);
        builder.UseOrleans(silo => PocSilo.Configure(silo, settings.Orleans, settings.Redis, settings.SiloPort, settings.GatewayPort));
        builder.AddBackendServices();
        builder.Services
            .AddSingleton(settings.Probes)
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddScoped<ICommandHandler<TenantProbe>, TenantProbeHandler>()
            .AddAggregatesFromAssemblyContaining<IntentScenario>()
            .AddTrustedType<TenantProbe>()
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

        var host = builder.Build();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
            await context.Database.ExecuteSqlRawAsync("DELETE FROM outbox_entry");
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await host.StartAsync(startTimeout.Token);
        return host;
    }

    /// <summary>Records a probe command for <paramref name="tenantId"/> without handing it over, as a host that died right after the record leaves it.</summary>
    public static async Task<Guid> RecordAsync(IHost host, Guid tenantId, Guid probeId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var session = PocSessions.For(tenantId);
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(session);
        var intentId = Guid.CreateVersion7();
        await scope.ServiceProvider.GetRequiredService<IntentRecorder>().RecordAsync(intentId, new TenantProbe(Guid.NewGuid(), probeId), session, aggregateId: null, heavy: false, CancellationToken.None);
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
    BusEnvelopeIntegrityMode? IntegrityMode = null);

/// <summary>A command whose handler records the tenant it ran under.</summary>
public sealed record TenantProbe(Guid AggregateId, Guid ProbeId) : ICommand;

/// <summary>Which tenant each probe ran under, and when; and every log entry the host wrote.</summary>
public sealed class RecordedIntentProbes
{
    public ConcurrentDictionary<Guid, (Guid Tenant, DateTimeOffset At)> Ran { get; } = new();

    public CapturedLogs Logs { get; } = new();
}

public sealed class TenantProbeHandler(RecordedIntentProbes probes, ISessionContextProvider sessions) : ICommandHandler<TenantProbe>
{
    public Task HandleAsync(TenantProbe command, CancellationToken cancellationToken)
    {
        probes.Ran[command.ProbeId] = (sessions.Current?.TenantId ?? Guid.Empty, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }
}
