namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Applies the registered <see cref="IEventUpcaster"/> chain to a persisted event payload on read,
/// before the payload is resolved to a runtime type and deserialized. Consumed by the event-mapping
/// layer; a host with no upcasters registered gets a transparent pass-through.
/// </summary>
/// <remarks>
/// Implementations resolve upcasters by <see cref="IEventUpcaster.SourceEventTypeName"/> (version-
/// independent match) and apply them in sequence — the target of one hop becomes the source lookup for
/// the next — until no upcaster matches the current type name.
/// </remarks>
public interface IEventUpcasterPipeline
{
    /// <summary>
    /// Runs the upcaster chain for a single persisted event.
    /// </summary>
    /// <param name="eventTypeName">The event type name as persisted in the event row.</param>
    /// <param name="dataJson">The event payload as persisted (JSON).</param>
    /// <returns>
    /// The effective type name and payload after upcasting. When no upcaster matches, the inputs are
    /// returned unchanged.
    /// </returns>
    /// <exception cref="System.InvalidOperationException">The upcaster chain is cyclic.</exception>
    UpcastedEvent Upcast(string eventTypeName, string dataJson);
}

/// <summary>
/// The result of running the <see cref="IEventUpcasterPipeline"/>: the effective event type name and
/// JSON payload after all matching upcasters have been applied.
/// </summary>
/// <param name="EventTypeName">The type name the payload should now be resolved and deserialized as.</param>
/// <param name="DataJson">The (possibly rewritten) JSON payload.</param>
public readonly record struct UpcastedEvent(string EventTypeName, string DataJson);
