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
        Message = "Command {IntentId} was handed over again after its hand-over was lost.")]
    public static partial void LogCommandResumed(this ILogger logger, Guid intentId);

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
        EventId = LogEvents.Orleans.CompletionFlushFailed,
        Level = LogLevel.Warning,
        Message = "Removing {Count} completed intents failed; the drain resumes them and their handlers may run again.")]
    public static partial void LogCompletionFlushFailed(this ILogger logger, Exception exception, int count);
}
