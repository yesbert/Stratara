using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Orleans.Configuration;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Checkpoints;
using Stratara.Orleans.Hosting;
using Stratara.Orleans.Projections;

namespace Stratara.Orleans.EntityFrameworkCore.Hosting;

/// <summary>
/// The reset against PostgreSQL: the runtime's reminder and membership tables, scoped to the host's service and
/// cluster so another deployment in the same database keeps its state; the checkpoints of the store readers the host
/// registers, so another consumer's checkpoints in the same read store stay;
/// and the grain directory through the cleanup the host supplies, because the directory's backend is its choice.
/// </summary>
/// <typeparam name="TReadContext">The read context that holds the checkpoint table.</typeparam>
internal sealed class ExecutionModelReset<TReadContext>(
    IDbContextFactory<TReadContext> readContextFactory,
    IOptions<ClusterOptions> cluster,
    ExecutionModelResetSettings settings,
    IEnumerable<INudgeTarget> storeReaders,
    IServiceProvider services) : IExecutionModelReset
    where TReadContext : DbContext
{
    public async Task<ExecutionModelResetReport> ResetAsync(CancellationToken cancellationToken = default)
    {
        int reminders;
        int membership;
        await using (var connection = new NpgsqlConnection(settings.RuntimeConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            reminders = await DeleteAsync(connection, "orleansreminderstable", "serviceid", cluster.Value.ServiceId, cancellationToken);
            membership = await DeleteAsync(connection, "orleansmembershiptable", "deploymentid", cluster.Value.ClusterId, cancellationToken);
            await DeleteAsync(connection, "orleansmembershipversiontable", "deploymentid", cluster.Value.ClusterId, cancellationToken);
        }

        int checkpoints;
        await using (var context = await readContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var consumers = storeReaders.SelectMany(reader => reader.ConsumerNames).Distinct(StringComparer.Ordinal).ToList();
            checkpoints = consumers.Count == 0
                ? 0
                : await context.Set<ProjectionCheckpoint>().Where(c => consumers.Contains(c.Projection)).ExecuteDeleteAsync(cancellationToken);
        }

        var directoryEntries = await settings.ClearDirectory(services, cancellationToken);
        return new ExecutionModelResetReport(reminders, membership, checkpoints, directoryEntries);
    }

    /// <summary>Deletes the rows of one deployment; a table the runtime never created holds none.</summary>
    private static async Task<int> DeleteAsync(NpgsqlConnection connection, string table, string column, string value, CancellationToken cancellationToken)
    {
        await using (var exists = new NpgsqlCommand("SELECT to_regclass(@table)::text", connection))
        {
            exists.Parameters.AddWithValue("table", table);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not string)
            {
                return 0;
            }
        }

        await using var delete = new NpgsqlCommand($"DELETE FROM {table} WHERE {column} = @value", connection);
        delete.Parameters.AddWithValue("value", value);
        return await delete.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Where the runtime keeps its tables, and how the host clears its grain directory.</summary>
/// <param name="RuntimeConnectionString">The database of the runtime's reminder and membership tables.</param>
/// <param name="ClearDirectory">Removes the directory entries of the host's cluster and returns how many.</param>
internal sealed record ExecutionModelResetSettings(string RuntimeConnectionString, Func<IServiceProvider, CancellationToken, Task<long>> ClearDirectory);
