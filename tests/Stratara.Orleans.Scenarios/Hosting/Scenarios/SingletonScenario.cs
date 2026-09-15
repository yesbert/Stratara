using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Stratara.Abstractions.Singleton;

namespace Stratara.Orleans.IntegrationTests.Hosting.Scenarios;

/// <summary>
/// A silo running one singleton work that records, in PostgreSQL, which silo ran it and when, so a test can kill
/// the silo running it and watch another silo of the cluster take it over. Commands: <c>last-silo</c>,
/// <c>runs silo</c>, <c>runs-since silo sinceUnixMs</c>; a silo is named by its silo port.
/// </summary>
public sealed class SingletonScenario : IPocScenario
{
    public async Task<IHost> BuildAsync(PocHostSettings settings)
    {
        await PocSilo.EnsureSchemaAsync(settings.OrleansConnectionString);
        var runs = new SingletonRunTable(settings.StoreConnectionString, settings.SiloPort);
        await runs.EnsureSchemaAsync();

        var builder = PocHosting.CreateBuilder();
        builder.UseOrleans(silo => settings.ConfigureSilo(silo));
        builder.Services
            .AddSingleton(runs)
            .AddStrataraSingletonWork<TakeoverProbeWork>(options =>
            {
                // A keep-alive below the production minimum reminder period, which only the test profile lowers.
                if (settings.Profile == PocSiloProfile.Test)
                {
                    options.KeepAlivePeriod = TimeSpan.FromSeconds(5);
                }
            });

        return builder.Build();
    }

    public async Task<string> HandleAsync(IServiceProvider services, string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var runs = services.GetRequiredService<SingletonRunTable>();

        return parts[0] switch
        {
            "last-silo" => await runs.LastSiloAsync(),
            "runs" => (await runs.RunsAsync(int.Parse(parts[1], CultureInfo.InvariantCulture), since: null)).ToString(CultureInfo.InvariantCulture),
            "runs-since" => (await runs.RunsAsync(
                int.Parse(parts[1], CultureInfo.InvariantCulture),
                DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(parts[2], CultureInfo.InvariantCulture)))).ToString(CultureInfo.InvariantCulture),
            _ => "error unknown command " + parts[0],
        };
    }
}

/// <summary>Runs every half second and records which silo ran it.</summary>
public sealed class TakeoverProbeWork(SingletonRunTable runs) : ISingletonWork
{
    public string Name => "takeover-probe";

    public TimeSpan Period => TimeSpan.FromMilliseconds(500);

    public Task RunAsync(CancellationToken cancellationToken) => runs.RecordAsync(cancellationToken);
}

/// <summary>The runs of the probe work, shared by every silo of the cluster through one store.</summary>
public sealed class SingletonRunTable(string connectionString, int silo)
{
    public async Task EnsureSchemaAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS poc_singleton_run (id bigserial PRIMARY KEY, silo int NOT NULL, ran_at timestamptz NOT NULL DEFAULT clock_timestamp())",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task RecordAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("INSERT INTO poc_singleton_run (silo) VALUES (@silo)", connection);
        command.Parameters.AddWithValue("silo", silo);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string> LastSiloAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT silo FROM poc_singleton_run ORDER BY id DESC LIMIT 1", connection);
        return await command.ExecuteScalarAsync() is int last ? last.ToString(CultureInfo.InvariantCulture) : "none";
    }

    public async Task<long> RunsAsync(int ofSilo, DateTimeOffset? since)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM poc_singleton_run WHERE silo = @silo AND ran_at >= @since", connection);
        command.Parameters.AddWithValue("silo", ofSilo);
        command.Parameters.AddWithValue("since", since ?? DateTimeOffset.UnixEpoch);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
