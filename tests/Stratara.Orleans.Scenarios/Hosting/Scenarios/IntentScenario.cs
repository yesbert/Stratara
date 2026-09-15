using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.IntegrationTests.Hosting.Scenarios;

/// <summary>
/// A backend host whose <c>ICommandOutboxDispatcher</c> is the durable-intent one and whose outbox
/// drain runs as singleton work. The probe command waits before it records itself in a table, which
/// opens the window a kill lands in. Commands: <c>enqueue aggregateId delayMs</c>,
/// <c>enqueue-heavy aggregateId delayMs</c>, <c>enqueue-failing aggregateId</c>,
/// <c>enqueue-ordered target sequence delayMs</c>, <c>heal aggregateId</c>,
/// <c>return-kept aggregateId</c>, <c>applied aggregateId</c>, <c>applications aggregateId</c>,
/// <c>intent aggregateId</c>, <c>order target</c>, <c>outbox-count</c>.
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
            .AddScoped<ICommandHandler<FailUntilHealed>, FailUntilHealedHandler>()
            .AddScoped<ICommandHandler<RecordInOrder>, RecordInOrderHandler>()
            .AddAggregatesFromAssemblyContaining<IntentScenario>()
            .AddTrustedType<RecordApplied>()
            .AddTrustedType<RecordAppliedHeavily>()
            .AddTrustedType<FailUntilHealed>()
            .AddTrustedType<RecordInOrder>()
            .AddSingleton(new AppliedTable(settings.StoreConnectionString))
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher(options => options.IntentGrace = TimeSpan.FromSeconds(2))
            .AddStrataraIntentStore<PocWriteDbContext>()
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
            case "enqueue-failing":
            case "enqueue-ordered":
            {
                await using var scope = services.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(NewSession());
                var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
                var aggregateId = Guid.Parse(parts[1]);
                var id = parts[0] switch
                {
                    "enqueue-heavy" => await dispatcher.EnqueueCommandAsync(new RecordAppliedHeavily(aggregateId, Int(parts[2]))),
                    "enqueue-failing" => await dispatcher.EnqueueCommandAsync(new FailUntilHealed(aggregateId)),
                    "enqueue-ordered" => await dispatcher.EnqueueCommandAsync(new RecordInOrder(aggregateId, Int(parts[2]), Int(parts[3]), "sealed note " + parts[2])),
                    _ => await dispatcher.EnqueueCommandAsync(new RecordApplied(aggregateId, Int(parts[2]))),
                };
                return "enqueued " + id;
            }
            case "heal":
                await applied.HealAsync(Guid.Parse(parts[1]));
                return "ok";
            case "return-kept":
                return (await applied.ReturnKeptAsync(Guid.Parse(parts[1]))).ToString(CultureInfo.InvariantCulture);
            case "applied":
                return (await applied.IsAppliedAsync(Guid.Parse(parts[1]))) ? "true" : "false";
            case "applications":
                return (await applied.ApplicationsAsync(Guid.Parse(parts[1]))).ToString(CultureInfo.InvariantCulture);
            case "applied-count":
                return (await applied.AppliedCountAsync()).ToString(CultureInfo.InvariantCulture);
            case "intent":
                return await applied.IntentAsync(Guid.Parse(parts[1]));
            case "order":
                return await applied.OrderAsync(Guid.Parse(parts[1]));
            case "outbox-count":
                return (await applied.OutboxCountAsync()).ToString(CultureInfo.InvariantCulture);
            case "load":
                LoadGenerator.Start(services, Int(parts[1]), Int(parts[2]));
                return "ok";
            default:
                return "error unknown command " + parts[0];
        }
    }

    private static int Int(string value) => int.Parse(value, CultureInfo.InvariantCulture);

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

/// <summary>Fails on every attempt until its aggregate is healed, then records itself applied.</summary>
public sealed record FailUntilHealed(Guid AggregateId) : ICommand, IAggregateScopedCommand;

/// <summary>
/// Names its aggregate only through the interface, so its JSON carries no <c>AggregateId</c>, and seals
/// a note: a resume that read the aggregate from the payload would not find it.
/// </summary>
public sealed record RecordInOrder(Guid Target, int Sequence, int DelayMs, [property: EncryptData] string Note) : ICommand, IAggregateScopedCommand
{
    Guid IAggregateScopedCommand.AggregateId => Target;
}

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

public sealed class FailUntilHealedHandler(AppliedTable applied) : ICommandHandler<FailUntilHealed>
{
    public async Task HandleAsync(FailUntilHealed command, CancellationToken cancellationToken)
    {
        if (!await applied.IsHealedAsync(command.AggregateId))
        {
            throw new InvalidOperationException($"probe failure for {command.AggregateId}");
        }

        await applied.MarkAsync(command.AggregateId);
    }
}

public sealed class RecordInOrderHandler(AppliedTable applied) : ICommandHandler<RecordInOrder>
{
    public async Task HandleAsync(RecordInOrder command, CancellationToken cancellationToken)
    {
        await Task.Delay(command.DelayMs, cancellationToken);
        await applied.RecordOrderAsync(command.Target, command.Sequence);
    }
}

/// <summary>The "append" the probe commands stand for, and the probes the tests read the outcome from.</summary>
public sealed class AppliedTable(string connectionString)
{
    public static async Task EnsureSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            CREATE TABLE IF NOT EXISTS poc_applied (aggregate_id uuid PRIMARY KEY, applied_at timestamptz NOT NULL, applications int NOT NULL DEFAULT 1);
            CREATE TABLE IF NOT EXISTS poc_healed (aggregate_id uuid PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS poc_order (aggregate_id uuid NOT NULL, sequence int NOT NULL, recorded_at timestamptz NOT NULL DEFAULT clock_timestamp());
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task MarkAsync(Guid aggregateId) =>
        await ExecuteAsync(
            """
            INSERT INTO poc_applied (aggregate_id, applied_at) VALUES (@id, now())
            ON CONFLICT (aggregate_id) DO UPDATE SET applications = poc_applied.applications + 1
            """,
            aggregateId);

    public Task HealAsync(Guid aggregateId) =>
        ExecuteAsync("INSERT INTO poc_healed (aggregate_id) VALUES (@id) ON CONFLICT DO NOTHING", aggregateId);

    public async Task<bool> IsHealedAsync(Guid aggregateId) =>
        await ScalarAsync("SELECT 1 FROM poc_healed WHERE aggregate_id = @id", aggregateId) is not null;

    /// <summary>The operator's return of a kept command: the statement the operations page documents.</summary>
    public Task<int> ReturnKeptAsync(Guid aggregateId) =>
        ExecuteAsync("UPDATE outbox_entry SET kept_at = NULL, attempt_count = 0 WHERE aggregate_id = @id AND kept_at IS NOT NULL", aggregateId);

    public Task RecordOrderAsync(Guid aggregateId, int sequence) =>
        ExecuteAsync("INSERT INTO poc_order (aggregate_id, sequence) VALUES (@id, " + sequence.ToString(CultureInfo.InvariantCulture) + ")", aggregateId);

    public async Task<string> OrderAsync(Guid aggregateId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT sequence FROM poc_order WHERE aggregate_id = @id ORDER BY recorded_at", connection);
        command.Parameters.AddWithValue("id", aggregateId);
        var sequences = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sequences.Add(reader.GetInt32(0).ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(",", sequences);
    }

    /// <summary>The recorded command for an aggregate: <c>attempts=N kept=true|false</c>, or <c>none</c> once it is gone.</summary>
    public async Task<string> IntentAsync(Guid aggregateId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT attempt_count, kept_at IS NOT NULL FROM outbox_entry WHERE aggregate_id = @id", connection);
        command.Parameters.AddWithValue("id", aggregateId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? $"attempts={reader.GetInt32(0).ToString(CultureInfo.InvariantCulture)} kept={(reader.GetBoolean(1) ? "true" : "false")}"
            : "none";
    }

    public async Task<long> AppliedCountAsync() =>
        (long)(await ScalarAsync("SELECT count(*) FROM poc_applied", null) ?? 0L);

    public async Task<int> ApplicationsAsync(Guid aggregateId) =>
        (int)(await ScalarAsync("SELECT applications FROM poc_applied WHERE aggregate_id = @id", aggregateId) ?? 0);

    public async Task<bool> IsAppliedAsync(Guid aggregateId) =>
        await ScalarAsync("SELECT 1 FROM poc_applied WHERE aggregate_id = @id", aggregateId) is not null;

    public async Task<long> OutboxCountAsync() =>
        (long)(await ScalarAsync("SELECT count(*) FROM outbox_entry", null) ?? 0L);

    private async Task<int> ExecuteAsync(string sql, Guid aggregateId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", aggregateId);
        return await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, Guid? aggregateId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        if (aggregateId is { } id)
        {
            command.Parameters.AddWithValue("id", id);
        }

        return await command.ExecuteScalarAsync();
    }
}
