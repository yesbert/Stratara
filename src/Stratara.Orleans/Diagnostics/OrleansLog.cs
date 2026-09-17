using Microsoft.Extensions.Logging;
using Stratara.Diagnostics;

namespace Stratara.Orleans.Diagnostics;

/// <summary>Source-generated log messages of the Orleans execution model, in the <c>117_000s</c> band.</summary>
internal static partial class OrleansLog
{
    [LoggerMessage(
        EventId = LogEvents.Orleans.StoreReaderStarted,
        Level = LogLevel.Information,
        Message = "Store reader for {Consumer} started on partition {Partition}.")]
    public static partial void LogStoreReaderStarted(this ILogger logger, string consumer, int partition);

    [LoggerMessage(
        EventId = LogEvents.Orleans.StoreReaderStopped,
        Level = LogLevel.Information,
        Message = "Store reader for {Consumer} stopped on partition {Partition}.")]
    public static partial void LogStoreReaderStopped(this ILogger logger, string consumer, int partition);

    [LoggerMessage(
        EventId = LogEvents.Orleans.CommandRecorded,
        Level = LogLevel.Debug,
        Message = "Command {IntentId} was recorded before its dispatch returned.")]
    public static partial void LogCommandRecorded(this ILogger logger, Guid intentId);

    [LoggerMessage(
        EventId = LogEvents.Orleans.CommandResumed,
        Level = LogLevel.Information,
        Message = "Command {IntentId} was handed over again after its hand-over lapsed; attempt {Attempt}.")]
    public static partial void LogCommandResumed(this ILogger logger, Guid intentId, int attempt);

    [LoggerMessage(
        EventId = LogEvents.Orleans.PartitionStalled,
        Level = LogLevel.Warning,
        Message = "Store reader for {Consumer} stopped on partition {Partition} at entry {EntryId} (sequence {SequenceNumber}); its checkpoint stays before the entry.")]
    public static partial void LogPartitionStalled(this ILogger logger, Exception exception, string consumer, int partition, Guid entryId, long sequenceNumber);

    [LoggerMessage(
        EventId = LogEvents.Orleans.EntryAttemptFailed,
        Level = LogLevel.Warning,
        Message = "Store reader for {Consumer} failed to apply entry {EntryId} on partition {Partition}; attempt {Attempt}.")]
    public static partial void LogEntryAttemptFailed(this ILogger logger, Exception exception, string consumer, int partition, Guid entryId, int attempt);

    [LoggerMessage(
        EventId = LogEvents.Orleans.CatchUpFaulted,
        Level = LogLevel.Error,
        Message = "A catch-up started by a wake-up failed for {Consumer} on partition {Partition}; the next wake-up or poll reads again.")]
    public static partial void LogCatchUpFaulted(this ILogger logger, Exception exception, string consumer, int partition);

    [LoggerMessage(
        EventId = LogEvents.Orleans.CommandKept,
        Level = LogLevel.Warning,
        Message = "Command {IntentId} was kept for an operator after {Attempts} attempts; last failure: {LastFailure}.")]
    public static partial void LogCommandKept(this ILogger logger, Guid intentId, int attempts, string lastFailure);

    [LoggerMessage(
        EventId = LogEvents.Orleans.PermitReleasedByExpiry,
        Level = LogLevel.Warning,
        Message = "Heavy-work permit of unit {UnitId} held by silo {Holder} was released: {Reason}.")]
    public static partial void LogPermitReleasedByExpiry(this ILogger logger, Guid unitId, string holder, string reason);

    [LoggerMessage(
        EventId = LogEvents.Orleans.DirectoryCheckFailed,
        Level = LogLevel.Error,
        Message = "No grain directory is registered under '{DirectoryName}'; the silo does not start. Register one with AddStrataraOrleans.")]
    public static partial void LogDirectoryCheckFailed(this ILogger logger, string directoryName);

    [LoggerMessage(
        EventId = LogEvents.Orleans.CompletionFlushFailed,
        Level = LogLevel.Warning,
        Message = "Removing {Count} completed intents failed; the drain resumes them and their handlers may run again.")]
    public static partial void LogCompletionFlushFailed(this ILogger logger, Exception exception, int count);

    [LoggerMessage(
        EventId = LogEvents.Orleans.IntentRenewalFailed,
        Level = LogLevel.Warning,
        Message = "Renewing the hand-over of running command {IntentId} failed; the next renewal tries again.")]
    public static partial void LogIntentRenewalFailed(this ILogger logger, Exception exception, Guid intentId);

    [LoggerMessage(
        EventId = LogEvents.Orleans.PermitRenewalLost,
        Level = LogLevel.Warning,
        Message = "Heavy-work permit of running unit {UnitId} was no longer held when renewed; it is taken again.")]
    public static partial void LogPermitRenewalLost(this ILogger logger, Guid unitId);

    [LoggerMessage(
        EventId = LogEvents.Orleans.PermitReclaimRefused,
        Level = LogLevel.Warning,
        Message = "Running heavy unit {UnitId} on {Holder} was refused its permit when it registered again; it runs outside the cluster-wide bound and asks again at every renewal.")]
    public static partial void LogPermitReclaimRefused(this ILogger logger, Guid unitId, string holder);

    [LoggerMessage(
        EventId = LogEvents.Orleans.PermitReleaseFailed,
        Level = LogLevel.Warning,
        Message = "Releasing the heavy-work permit of unit {UnitId} failed; its lease releases it.")]
    public static partial void LogPermitReleaseFailed(this ILogger logger, Exception exception, Guid unitId);

    [LoggerMessage(
        EventId = LogEvents.Orleans.RecordedCommandsWithoutIntentStore,
        Level = LogLevel.Warning,
        Message = "The outbox drain found commands recorded by the execution model but no intent store is registered on this silo; register AddStrataraIntentStore where the drain runs, or they are not resumed.")]
    public static partial void LogRecordedCommandsWithoutIntentStore(this ILogger logger);

    [LoggerMessage(
        EventId = LogEvents.Orleans.IntentAttemptFailed,
        Level = LogLevel.Warning,
        Message = "An attempt to run recorded command {IntentId} ({CommandType}) for aggregate {AggregateId} failed; the failure is recorded with the command, which is resumed within its bound.")]
    public static partial void LogIntentAttemptFailed(this ILogger logger, Exception exception, Guid intentId, string commandType, Guid? aggregateId);

    [LoggerMessage(
        EventId = LogEvents.Orleans.HandOverFailed,
        Level = LogLevel.Warning,
        Message = "Handing recorded command {IntentId} over to its grain failed (aggregate {AggregateId}, heavy {Heavy}); the drain hands it over again after the grace.")]
    public static partial void LogHandOverFailed(this ILogger logger, Exception exception, Guid intentId, Guid? aggregateId, bool heavy);
}
