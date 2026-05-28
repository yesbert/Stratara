using Microsoft.Extensions.Logging;
using Stratara.Diagnostics;

namespace Stratara.Shared.Diagnostics.Extensions;

/// <summary>Source-generated logger extensions for aggregate update side-effects detected by projections.</summary>
public static partial class LoggerUpdateExtensions
{
    /// <summary>Logs that an update was attempted against an aggregate that has already been deleted.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="aggregateId">The aggregate identifier.</param>
    [LoggerMessage(
        EventId = LogEvents.Update.AggregateDeleted,
        Level = LogLevel.Warning,
        Message = "Aggregate {AggregateId} is deleted when trying to apply update.")]
    public static partial void LogUpdateAggregateDeleted(this ILogger logger, Guid aggregateId);

    /// <summary>Logs that an update was attempted against an aggregate that resolved to <c>null</c>.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="aggregateId">The aggregate identifier.</param>
    [LoggerMessage(
        EventId = LogEvents.Update.AggregateNull,
        Level = LogLevel.Error,
        Message = "Aggregate {AggregateId} is null when trying to apply update.")]
    public static partial void LogUpdateAggregateNull(this ILogger logger, Guid aggregateId);
}
