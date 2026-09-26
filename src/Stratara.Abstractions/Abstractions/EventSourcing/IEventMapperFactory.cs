using Stratara.Contracts.Messages;


namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Deserialises persisted <see cref="EventStreamEntry"/> rows or in-flight
/// <see cref="EventMessage"/> envelopes into materialised <see cref="IEvent"/> instances.
/// </summary>
public interface IEventMapperFactory
{
    /// <summary>Map persisted stream entries to materialised events.</summary>
    /// <param name="entries">The persisted stream entries to map.</param>
    /// <param name="cancellationToken">Token observed during async secure deserialization.</param>
    Task<IReadOnlyList<IEvent>> MapToEventsAsync(IEnumerable<EventStreamEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>Map wire-level event messages (e.g. from a bus) to materialised events.</summary>
    /// <param name="messages">The event messages to map.</param>
    /// <param name="cancellationToken">Token observed during async secure deserialization.</param>
    Task<IReadOnlyList<IEvent>> MapToEventsAsync(IEnumerable<EventMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>Map the persisted stream entries a reader has a use for, leaving every other entry unread.</summary>
    /// <param name="entries">The persisted stream entries.</param>
    /// <param name="relevance">Which events the reader has a use for.</param>
    /// <param name="cancellationToken">Token observed during async secure deserialization.</param>
    /// <returns>The relevant events, in the entries' order.</returns>
    /// <remarks>
    /// The default implementation maps every entry and keeps the relevant events, so an entry whose type is not
    /// registered still fails. The framework's mapper overrides it and resolves and decrypts only what
    /// <paramref name="relevance"/> accepts.
    /// </remarks>
    async Task<IReadOnlyList<IEvent>> MapToEventsAsync(IEnumerable<EventStreamEntry> entries, EventRelevance relevance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relevance);
        var events = await MapToEventsAsync(entries, cancellationToken);
        return events.Where(e => relevance.Includes(e.Data.GetType())).ToList();
    }

    /// <summary>Map the wire-level event messages a reader has a use for, leaving every other message unread.</summary>
    /// <param name="messages">The event messages.</param>
    /// <param name="relevance">Which events the reader has a use for.</param>
    /// <param name="cancellationToken">Token observed during async secure deserialization.</param>
    /// <returns>The relevant events, in the messages' order.</returns>
    /// <remarks>
    /// The default implementation maps every message and keeps the relevant events, so a message whose type is not
    /// registered still fails. The framework's mapper overrides it and resolves and decrypts only what
    /// <paramref name="relevance"/> accepts.
    /// </remarks>
    async Task<IReadOnlyList<IEvent>> MapToEventsAsync(IEnumerable<EventMessage> messages, EventRelevance relevance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relevance);
        var events = await MapToEventsAsync(messages, cancellationToken);
        return events.Where(e => relevance.Includes(e.Data.GetType())).ToList();
    }
}
