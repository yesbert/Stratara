namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Reports that the entity a fact refers to does not exist in the handler's read model yet, so the fact
/// cannot be applied until the fact that created the entity has been.
/// </summary>
/// <remarks>
/// Throw it from a projection or saga handler where the row a fact updates is absent. The framework
/// treats it unlike any other failure: the projection and saga workers retry the bundle in-process a
/// bounded number of times with a short backoff — about three seconds in total — holding no aggregate
/// lock while they wait, so the creating fact can be applied in between. Only once the retries are
/// exhausted does the bundle fail as an unhandled failure does. Across the consumers of one
/// subscription the framework does not promise that a fact arrives after the fact that precedes it;
/// this exception is how a handler tolerates that without acknowledging a fact it never applied.
/// </remarks>
public sealed class PrecedingFactMissingException : Exception
{
    /// <summary>Initializes the exception for the fact that could not be applied yet.</summary>
    /// <param name="streamId">The stream the fact belongs to.</param>
    /// <param name="eventTypeName">The type name of the fact that was being applied.</param>
    /// <param name="innerException">The failure that revealed the absence, if any.</param>
    public PrecedingFactMissingException(Guid streamId, string eventTypeName, Exception? innerException = null)
        : base($"The fact {eventTypeName} on stream {streamId} refers to an entity that has not been applied yet.", innerException)
    {
        StreamId = streamId;
        EventTypeName = eventTypeName;
    }

    /// <summary>The stream the fact belongs to.</summary>
    public Guid StreamId { get; }

    /// <summary>The type name of the fact that was being applied.</summary>
    public string EventTypeName { get; }
}
