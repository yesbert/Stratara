using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.Reflections;

namespace Stratara.Shared.EventSourcing;

/// <summary>
/// Concrete implementation of <see cref="IEvent{TEvent}"/> that wraps a strongly-typed event payload
/// together with the stream identity, version, and tenant / actor context required by the event store.
/// </summary>
/// <typeparam name="TEvent">CLR type of the wrapped event payload.</typeparam>
/// <param name="Id">Stable event id (also the event-store row id).</param>
/// <param name="Version">Monotonic version of the event within its stream.</param>
/// <param name="Data">The typed event payload.</param>
/// <param name="StreamId">Id of the event stream / aggregate the event belongs to.</param>
/// <param name="TenantId">Data-owner tenant the event belongs to.</param>
/// <param name="UserId">Actor user id that produced the event (used in audit surfaces).</param>
/// <param name="AggregateTypeName">CLR type name of the aggregate whose state the event mutates.</param>
public sealed record Event<TEvent>(
    Guid Id,
    long Version,
    TEvent Data,
    Guid StreamId,
    Guid TenantId,
    Guid UserId,
    string AggregateTypeName = "Unknown") : IEvent<TEvent> where TEvent : notnull
{
    /// <inheritdoc/>
    object IEvent.Data => Data;

    /// <inheritdoc/>
    public string EventTypeName => Data.GetType().GetQualifiedTypeName();
}
