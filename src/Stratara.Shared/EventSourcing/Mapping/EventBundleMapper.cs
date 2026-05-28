using System.Text.Json;
using Stratara.Contracts.Session;
using Stratara.Abstractions.EventSourcing;
using Stratara.Contracts.Messages;

namespace Stratara.Shared.EventSourcing.Mapping;

/// <summary>
/// Conversion helpers that fold a batch of persisted <see cref="EventStreamEntry"/> rows together
/// with the originating <see cref="SessionContext"/> into a wire-level <see cref="EventBundle"/>
/// suitable for outbox / message-bus transport.
/// </summary>
public static class EventBundleMapper
{
    /// <summary>
    /// Maps a sequence of <see cref="EventStreamEntry"/> rows to a single <see cref="EventBundle"/>,
    /// embedding the supplied <see cref="SessionContext"/> as serialized JSON.
    /// </summary>
    /// <param name="entries">The persisted event-stream rows to wrap.</param>
    /// <param name="sessionContext">Session context to embed for handler-side replay.</param>
    /// <returns>An <see cref="EventBundle"/> carrying the converted <see cref="EventMessage"/> list and session JSON.</returns>
    public static EventBundle MapToEventBundle(this IEnumerable<EventStreamEntry> entries, SessionContext sessionContext)
    {
        var eventMessages = entries.MapToEventMessages();
        var sessionContextJson = JsonSerializer.Serialize(sessionContext);
        return new EventBundle(eventMessages, sessionContextJson);
    }

    private static List<EventMessage> MapToEventMessages(this IEnumerable<EventStreamEntry> entries) =>
        entries.Select(MapToEventMessage).ToList();

    private static EventMessage MapToEventMessage(this EventStreamEntry entry) =>
        new(
            entry.Id,
            entry.Version,
            entry.DataJson,
            entry.StreamId,
            entry.EventTypeName,
            entry.AggregateTypeName,
            entry.ActorTenantId,
            entry.ActorUserId,
            entry.TenantId,
            entry.UserId);
}
