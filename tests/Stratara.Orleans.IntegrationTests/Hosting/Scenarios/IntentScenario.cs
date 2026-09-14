using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.IntegrationTests.Hosting.Scenarios;

/// <summary>
/// A backend host whose <c>ICommandOutboxDispatcher</c> is the durable-intent one and whose outbox
/// drain runs as singleton work. The probe command waits before it records itself in a table, which
/// opens the window a kill lands in. Commands: <c>enqueue aggregateId delayMs</c>,
/// <c>applied aggregateId</c>, <c>outbox-count</c>.
/// </summary>
public sealed class IntentScenario : IPocScenario
{
    public async Task<IHost> BuildAsync(PocHostSettings settings)
    {
        await PocSilo.EnsureSchemaAsync(settings.OrleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = settings.StoreConnectionString,
            ["ConnectionStrings:rabbitmq"] = settings.RabbitConnectionString,
        });
        builder.UseOrleans(silo => settings.ConfigureSilo(silo));
        builder.AddBackendServices();
        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddScoped<ICommandHandler<RecordApplied>, RecordAppliedHandler>()
            .AddScoped<ICommandHandler<RecordAppliedHeavily>, RecordAppliedHeavilyHandler>()
            .AddAggregatesFromAssemblyContaining<IntentScenario>()
            .AddTrustedType<RecordApplied>()
            .AddTrustedType<RecordAppliedHeavily>()
            .AddSingleton(new AppliedTable(settings.StoreConnectionString))
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher(options => options.IntentGrace = TimeSpan.FromSeconds(2))
            .AddStrataraSingletonWork<OutboxDrainWork>(options => options.KeepAlivePeriod = settings.Profile == PocSiloProfile.Test ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1));

        if (settings.Profile == PocSiloProfile.Test)
        {
            // The kill tests want a lost hand-off resumed within seconds; a deployed silo drains at the default interval.
            builder.Services.Configure<OutboxDrainOptions>(options => options.PollingInterval = TimeSpan.FromSeconds(1));
        }

        var host = builder.Build();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
        }

        await AppliedTable.EnsureSchemaAsync(settings.StoreConnectionString);
        return host;
    }

    public async Task<string> HandleAsync(IServiceProvider services, string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var applied = services.GetRequiredService<AppliedTable>();

        switch (parts[0])
        {
            case "enqueue":
            case "enqueue-heavy":
            {
                await using var scope = services.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(NewSession());
                var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
                var aggregateId = Guid.Parse(parts[1]);
                var delay = int.Parse(parts[2], CultureInfo.InvariantCulture);
                var id = parts[0] == "enqueue-heavy"
                    ? await dispatcher.EnqueueCommandAsync(new RecordAppliedHeavily(aggregateId, delay))
                    : await dispatcher.EnqueueCommandAsync(new RecordApplied(aggregateId, delay));
                return "enqueued " + id;
            }
            case "applied":
                return (await applied.IsAppliedAsync(Guid.Parse(parts[1]))) ? "true" : "false";
            case "applied-count":
                return (await applied.AppliedCountAsync()).ToString(CultureInfo.InvariantCulture);
            case "outbox-count":
                return (await applied.OutboxCountAsync()).ToString(CultureInfo.InvariantCulture);
            case "load":
                LoadGenerator.Start(services, int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture));
                return "ok";
            default:
                return "error unknown command " + parts[0];
        }
    }

    private static SessionContext NewSession() => PocSessions.New();
}

/// <summary>
/// Dispatches probe commands at a steady rate on a task of its own, so the host answers the command
/// that started the load at once and the parent can sample it while the load runs.
/// </summary>
public static class LoadGenerator
{
    public static void Start(IServiceProvider services, int count, int ratePerSecond)
    {
        _ = Task.Run(async () =>
        {
            var interval = TimeSpan.FromSeconds(1.0 / ratePerSecond);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
            for (var i = 0; i < count; i++)
            {
                await dispatcher.EnqueueCommandAsync(new RecordApplied(Guid.NewGuid(), 0));
                var due = started + (long)((i + 1) * interval.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
                var wait = TimeSpan.FromSeconds((due - System.Diagnostics.Stopwatch.GetTimestamp()) / (double)System.Diagnostics.Stopwatch.Frequency);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait);
                }
            }
        });
    }
}

public sealed record RecordApplied(Guid AggregateId, int DelayMs) : ICommand, IAggregateScopedCommand;

public sealed record RecordAppliedHeavily(Guid AggregateId, int DelayMs) : ICommand, IAggregateScopedCommand, IHeavyCommand;

public sealed class RecordAppliedHandler(AppliedTable applied) : ICommandHandler<RecordApplied>
{
    public async Task HandleAsync(RecordApplied command, CancellationToken cancellationToken)
    {
        await Task.Delay(command.DelayMs, cancellationToken);
        await applied.MarkAsync(command.AggregateId);
    }
}

public sealed class RecordAppliedHeavilyHandler(AppliedTable applied) : ICommandHandler<RecordAppliedHeavily>
{
    public async Task HandleAsync(RecordAppliedHeavily command, CancellationToken cancellationToken)
    {
        await Task.Delay(command.DelayMs, cancellationToken);
        await applied.MarkAsync(command.AggregateId);
    }
}

/// <summary>The "append" the probe command stands for: a row per aggregate, written after the delay.</summary>
public sealed class AppliedTable(string connectionString)
{
    public static async Task EnsureSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS poc_applied (aggregate_id uuid PRIMARY KEY, applied_at timestamptz NOT NULL, applications int NOT NULL DEFAULT 1)", connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task MarkAsync(Guid aggregateId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO poc_applied (aggregate_id, applied_at) VALUES (@id, now())
            ON CONFLICT (aggregate_id) DO UPDATE SET applications = poc_applied.applications + 1
            """, connection);
        command.Parameters.AddWithValue("id", aggregateId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> AppliedCountAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM poc_applied", connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    public async Task<bool> IsAppliedAsync(Guid aggregateId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT 1 FROM poc_applied WHERE aggregate_id = @id", connection);
        command.Parameters.AddWithValue("id", aggregateId);
        return await command.ExecuteScalarAsync() is not null;
    }

    public async Task<long> OutboxCountAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM outbox_entry", connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
