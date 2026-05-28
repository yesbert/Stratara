namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Materialised event with its envelope metadata. Returned by
/// <see cref="IEventMapperFactory"/> when deserialising rows from the event stream.
/// </summary>
public interface IEvent
{
    /// <summary>Unique id of this event (V7 GUID).</summary>
    Guid Id { get; }

    /// <summary>The aggregate-relative version this event was appended at.</summary>
    long Version { get; }

    /// <summary>The deserialised event payload.</summary>
    object Data { get; }

    /// <summary>Id of the stream this event belongs to (= aggregate id).</summary>
    Guid StreamId { get; }

    /// <summary>Fully-qualified, version-independent type name of the event payload.</summary>
    string EventTypeName { get; }

    /// <summary>Fully-qualified, version-independent type name of the owning aggregate.</summary>
    string AggregateTypeName { get; }

    /// <summary>Subject (data-owner) tenant id.</summary>
    Guid TenantId { get; }

    /// <summary>Subject (data-owner) user id, or <see cref="Guid.Empty"/> if not user-scoped.</summary>
    Guid UserId { get; }
}

/// <summary>
/// Strongly-typed view of <see cref="IEvent"/> with a payload of <typeparamref name="TEvent"/>.
/// </summary>
/// <typeparam name="TEvent">The concrete event-payload type.</typeparam>
public interface IEvent<out TEvent> : IEvent where TEvent : notnull
{
    /// <summary>The deserialised event payload, strongly typed.</summary>
    new TEvent Data { get; }
}
