using Microsoft.Extensions.Logging;
using Stratara.Diagnostics;
using Stratara.Abstractions.Merging.ChangeTracking;

namespace Stratara.Shared.Diagnostics.Extensions;

/// <summary>Source-generated logger extensions for the projection change-set merge pipeline.</summary>
public static partial class LoggerChangeSetExtensions
{
    /// <summary>Logs that the aggregate referenced by a change-set could not be found in the event store.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="aggregateType">The aggregate type name.</param>
    /// <param name="aggregateId">The aggregate identifier that was not found.</param>
    [LoggerMessage(
        EventId = LogEvents.ChangeSet.AggregateNotFound,
        Level = LogLevel.Error,
        Message =
            "Failed to create change set for {AggregateType} with ID {AggregateId}: Aggregate not found in event store.")]
    public static partial void LogChangeSetNotFound(
        this ILogger logger,
        string aggregateType,
        Guid aggregateId);

    /// <summary>
    /// Logs that a change-set has been computed for an aggregate, listing the changed field
    /// names but never their values — values may carry <c>[EncryptData]</c>-protected PII that
    /// must not leak into the log pipeline (Round-3-Audit Finding R3-Sec-005).
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="changeCount">Number of fields changed.</param>
    /// <param name="fieldNames">Deferred-formatting wrapper that renders the changed property names only.</param>
    [LoggerMessage(
        EventId = LogEvents.ChangeSet.ChangeSetCreated,
        Level = LogLevel.Debug,
        Message = "Change set created for aggregate {AggregateId}: {ChangeCount} field(s) changed ({FieldNames}).")]
    public static partial void LogChangeSetCreated(
        this ILogger logger,
        Guid aggregateId,
        int changeCount,
        ChangeSetFieldNames fieldNames);

    /// <summary>
    /// Convenience overload that turns an <see cref="IReadOnlyList{ChangeDetail}"/> into the
    /// safe (names-only) form before forwarding to the source-generated logger method.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="changeSet">The list of detected field changes — only the names are logged.</param>
    public static void LogChangeSetCreated(this ILogger logger, Guid aggregateId, IReadOnlyList<ChangeDetail> changeSet)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        logger.LogChangeSetCreated(aggregateId, changeSet.Count, new ChangeSetFieldNames(changeSet));
    }

    /// <summary>Logs that a change-set has been applied to an aggregate.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="aggregateId">The aggregate identifier.</param>
    [LoggerMessage(
        EventId = LogEvents.ChangeSet.ChangeSetApplied,
        Level = LogLevel.Debug,
        Message = "Change set applied to aggregate {AggregateId}.")]
    public static partial void LogChangeSetApplied(
        this ILogger logger,
        Guid aggregateId);

    /// <summary>Logs that the computed change-set was empty and no events were emitted.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="aggregateId">The aggregate identifier.</param>
    [LoggerMessage(
        EventId = LogEvents.ChangeSet.NoChangesToApplied,
        Level = LogLevel.Debug,
        Message = "No changes to apply for aggregate {AggregateId}.")]
    public static partial void LogNoChangesToApplied(
        this ILogger logger,
        Guid aggregateId);
}
