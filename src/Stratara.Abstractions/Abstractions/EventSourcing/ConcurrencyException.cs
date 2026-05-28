namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Thrown by <see cref="IEventSource.SaveChangesAsync"/> when another writer committed
/// to the same stream first and the optimistic version check fails. Callers should
/// re-aggregate and retry the operation idempotently.
/// </summary>
public sealed class ConcurrencyException : Exception
{
    /// <summary>Initialise a new <see cref="ConcurrencyException"/>.</summary>
    /// <param name="streamId">The stream that lost the race.</param>
    /// <param name="aggregateTypeName">The aggregate type bound to the stream — for diagnostics.</param>
    /// <param name="innerException">Optional inner exception (typically the underlying DB exception).</param>
    public ConcurrencyException(Guid streamId, string aggregateTypeName, Exception? innerException = null)
        : base($"Concurrent write detected on stream {streamId} ({aggregateTypeName}).", innerException)
    {
        StreamId = streamId;
        AggregateTypeName = aggregateTypeName;
    }

    /// <summary>The stream id that experienced the concurrent write.</summary>
    public Guid StreamId { get; }

    /// <summary>The aggregate type bound to the stream.</summary>
    public string AggregateTypeName { get; }
}
