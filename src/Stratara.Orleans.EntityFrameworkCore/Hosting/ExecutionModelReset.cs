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
    private const string RemindersTable = "orleansreminderstable";
    private const string MembershipTable = "orleansmembershiptable";
    private const string MembershipVersionTable = "orleansmembershipversiontable";

    /// <exception cref="InvalidOperationException">A runtime table is absent under the named schema.</exception>
    public async Task<ExecutionModelResetReport> ResetAsync(CancellationToken cancellationToken = default)
    {
        int reminders;
        int membership;
        await using (var connection = new NpgsqlConnection(settings.RuntimeConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            foreach (var table in new[] { RemindersTable, MembershipTable, MembershipVersionTable })
            {
                await EnsureExistsAsync(connection, settings.Schema, table, cancellationToken);
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            reminders = await DeleteAsync(connection, transaction, settings.Schema, RemindersTable, "serviceid", cluster.Value.ServiceId, cancellationToken);
            membership = await DeleteAsync(connection, transaction, settings.Schema, MembershipTable, "deploymentid", cluster.Value.ClusterId, cancellationToken);
            await DeleteAsync(connection, transaction, settings.Schema, MembershipVersionTable, "deploymentid", cluster.Value.ClusterId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
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

    /// <summary>
    /// A runtime table that does not exist under the schema is a reset against the wrong database or schema, which
    /// would remove nothing and say so with a count of zero; it fails naming the table instead.
    /// </summary>
    /// <exception cref="InvalidOperationException">The table is absent.</exception>
    private static async Task EnsureExistsAsync(NpgsqlConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        await using var exists = new NpgsqlCommand("SELECT to_regclass(@qualified)::text", connection);
        exists.Parameters.AddWithValue("qualified", $"{Quote(schema)}.{Quote(table)}");
        if (await exists.ExecuteScalarAsync(cancellationToken) is string)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The Orleans runtime table {schema}.{table} does not exist, so the reset would remove nothing. Point the reset at the database that holds the reminder and membership tables, and name their schema where it is not the connection's default.");
    }

    /// <summary>Deletes the rows of one deployment.</summary>
    private static async Task<int> DeleteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string table, string column, string value, CancellationToken cancellationToken)
    {
        await using var delete = new NpgsqlCommand($"DELETE FROM {Quote(schema)}.{Quote(table)} WHERE {column} = @value", connection, transaction);
        delete.Parameters.AddWithValue("value", value);
        return await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>Where the runtime keeps its tables, and how the host clears its grain directory.</summary>
/// <param name="RuntimeConnectionString">The database of the runtime's reminder and membership tables.</param>
/// <param name="ClearDirectory">Removes the directory entries of the host's cluster and returns how many.</param>
/// <param name="Schema">The schema the runtime tables live in.</param>
internal sealed record ExecutionModelResetSettings(string RuntimeConnectionString, Func<IServiceProvider, CancellationToken, Task<long>> ClearDirectory, string Schema);
