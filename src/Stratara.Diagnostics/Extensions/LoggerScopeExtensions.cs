using Microsoft.Extensions.Logging;

namespace Stratara.Shared.Diagnostics.Extensions;

/// <summary>
/// Pre-baked logging-scope helpers for the create/update aggregate flows. Use these so that
/// every log entry inside the scope picks up the aggregate id automatically.
/// </summary>
public static class LoggerScopeExtensions
{
    /// <summary>
    /// Begin a logging scope tagged with <c>AggregateId</c> = <paramref name="aggregateId"/>
    /// for the create-aggregate flow.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="aggregateId">The id of the aggregate being created.</param>
    /// <returns>A disposable that ends the scope, or <c>null</c> when no scope provider is configured.</returns>
    public static IDisposable? BeginCreateAggregateScope(this ILogger logger, Guid aggregateId) =>
        logger.BeginScope("Creating aggregate {AggregateId}", aggregateId);

    /// <summary>
    /// Begin a logging scope tagged with <c>AggregateId</c> = <paramref name="aggregateId"/>
    /// for the update-aggregate flow.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="aggregateId">The id of the aggregate being updated.</param>
    /// <returns>A disposable that ends the scope, or <c>null</c> when no scope provider is configured.</returns>
    public static IDisposable? BeginUpdateAggregateScope(this ILogger logger, Guid aggregateId) =>
        logger.BeginScope("Updating aggregate {AggregateId}", aggregateId);
}
