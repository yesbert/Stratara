using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Stratara.Orleans.Timers;

namespace Stratara.Orleans.IntegrationTests.Hosting.Scenarios;

/// <summary>
/// A silo with the durable timers, whose owners and firings live in two PostgreSQL tables so that a
/// kill loses neither. Commands: <c>add-owner id</c>, <c>remove-owner id</c>,
/// <c>register owner purpose dueInMs</c>, <c>timers owner</c>, <c>firings owner</c>.
/// </summary>
public sealed class TimersScenario : IPocScenario
{
    public async Task<IHost> BuildAsync(PocHostSettings settings)
    {
        await PocSilo.EnsureSchemaAsync(settings.OrleansConnectionString);
        await PostgresTimerHost.EnsureSchemaAsync(settings.StoreConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.UseOrleans(silo => PocSilo.Configure(silo, settings.OrleansConnectionString, settings.RedisConnectionString, settings.SiloPort, settings.GatewayPort));
        builder.Services
            .AddStrataraDurableTimers(options => options.RetryPeriod = TimeSpan.FromSeconds(1))
            .AddSingleton(new PostgresTimerHost(settings.StoreConnectionString))
            .AddSingleton<ITimerOwners>(sp => sp.GetRequiredService<PostgresTimerHost>())
            .AddSingleton<ITimerHandler>(sp => sp.GetRequiredService<PostgresTimerHost>());

        return builder.Build();
    }

    public async Task<string> HandleAsync(IServiceProvider services, string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var timerHost = services.GetRequiredService<PostgresTimerHost>();
        var timers = services.GetRequiredService<IDurableTimers>();

        switch (parts[0])
        {
            case "add-owner":
                await timerHost.AddOwnerAsync(parts[1]);
                return "ok";
            case "remove-owner":
                await timerHost.RemoveOwnerAsync(parts[1]);
                return "ok";
            case "register":
                var dueIn = TimeSpan.FromMilliseconds(int.Parse(parts[3], CultureInfo.InvariantCulture));
                await timers.RegisterAsync(new TimerRegistration(parts[1], parts[2], DateTimeOffset.UtcNow + dueIn));
                return "ok";
            case "timers":
                return (await timers.ListAsync(parts[1])).Count.ToString(CultureInfo.InvariantCulture);
            case "firings":
                return (await timerHost.FiringsAsync(parts[1])).ToString(CultureInfo.InvariantCulture);
            default:
                return "error unknown command " + parts[0];
        }
    }
}

/// <summary>Owners and firings in PostgreSQL, so they outlive the process.</summary>
public sealed class PostgresTimerHost(string connectionString) : ITimerOwners, ITimerHandler
{
    public static async Task EnsureSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            CREATE TABLE IF NOT EXISTS poc_timer_owner (owner_id text PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS poc_timer_firing (
                id bigserial PRIMARY KEY,
                owner_id text NOT NULL,
                purpose text NOT NULL,
                due_at timestamptz NOT NULL,
                fired_at timestamptz NOT NULL);
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task AddOwnerAsync(string ownerId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("INSERT INTO poc_timer_owner (owner_id) VALUES (@id) ON CONFLICT DO NOTHING", connection);
        command.Parameters.AddWithValue("id", ownerId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task RemoveOwnerAsync(string ownerId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("DELETE FROM poc_timer_owner WHERE owner_id = @id", connection);
        command.Parameters.AddWithValue("id", ownerId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> ExistsAsync(string ownerId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1 FROM poc_timer_owner WHERE owner_id = @id", connection);
        command.Parameters.AddWithValue("id", ownerId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task OnDueAsync(TimerDue due, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "INSERT INTO poc_timer_firing (owner_id, purpose, due_at, fired_at) VALUES (@owner, @purpose, @due, @fired)", connection);
        command.Parameters.AddWithValue("owner", due.OwnerId);
        command.Parameters.AddWithValue("purpose", due.Purpose);
        command.Parameters.AddWithValue("due", due.DueAt);
        command.Parameters.AddWithValue("fired", due.FiredAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> FiringsAsync(string ownerId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM poc_timer_firing WHERE owner_id = @id", connection);
        command.Parameters.AddWithValue("id", ownerId);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
