namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Resolves an event envelope to a strongly-typed <see cref="IEvent{TEvent}"/> by
/// loading the runtime <see cref="Type"/> via the persisted, version-independent type
/// name. Used by projection handlers + sagas + the event-mapper.
/// </summary>
public interface IEventTypeResolver
{
    /// <summary>Cast or wrap <paramref name="event"/> into a typed envelope of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The concrete event-payload type.</typeparam>
    /// <param name="event">The untyped event envelope.</param>
    IEvent<T> Resolve<T>(IEvent @event) where T : notnull;

    /// <summary>
    /// Reflection-based variant returning the typed event envelope as <c>object</c>.
    /// Used by generic dispatch code that picks the handler by runtime <see cref="Type"/>.
    /// </summary>
    object ResolveDynamic(IEvent @event);

    /// <summary>Return the runtime <see cref="Type"/> of the event payload.</summary>
    Type GetEventDataType(IEvent @event);
}
