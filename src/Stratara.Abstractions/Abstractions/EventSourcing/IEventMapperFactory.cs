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
}
