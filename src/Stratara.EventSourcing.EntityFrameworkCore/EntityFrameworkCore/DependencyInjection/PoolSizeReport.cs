using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Npgsql;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Which databases a host has reported the pool size of: once per host, database, user and size, keyed without the
/// password, so a rotated credential neither logs again nor stays in memory. A database is counted as reported only once
/// the line was written, so a log level that suppressed it at first does not suppress it for good.
/// </summary>
internal sealed class PoolSizeReport
{
    private readonly ConcurrentDictionary<(string? Host, int Port, string? Database, string? Username, int MaxPoolSize, bool Named), byte> _reported = new();

    public void Report(NpgsqlConnectionStringBuilder connection, bool named, ILogger logger)
    {
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        if (_reported.TryAdd((connection.Host, connection.Port, connection.Database, connection.Username, connection.MaxPoolSize, named), 0))
        {
            logger.LogPoolSizeInEffect(connection.MaxPoolSize, named ? "the connection string" : "the framework's default", connection.Database ?? "(none)", connection.Host ?? "(none)");
        }
    }
}
