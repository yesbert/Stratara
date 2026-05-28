using System.Text.Json;
using Stratara.Abstractions.Outbox;

namespace Stratara.Shared.Outbox.Mapping;

/// <summary>
/// Conversion helpers that hydrate the JSON payload of an <see cref="OutboxEntry"/> back into a
/// typed message instance (e.g. <c>CommandEnvelope</c>, <c>EventBundle</c>).
/// </summary>
public static class OutboxEntryMapper
{
    /// <summary>
    /// Deserializes <see cref="OutboxEntry.DataJson"/> into the requested type
    /// <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target type to deserialize into.</typeparam>
    /// <param name="outboxEntry">The outbox row carrying the JSON payload.</param>
    /// <returns>The deserialized payload.</returns>
    /// <exception cref="JsonException">Thrown when the JSON payload cannot be deserialized to <typeparamref name="T"/>.</exception>
    public static T MapTo<T>(this OutboxEntry outboxEntry) =>
        JsonSerializer.Deserialize<T>(outboxEntry.DataJson) ??
        throw new JsonException("Could not deserialize the outbox entry data.");

    /// <summary>
    /// Deserializes a batch of <see cref="OutboxEntry"/> rows into typed <typeparamref name="T"/>
    /// instances. The result preserves the input order.
    /// </summary>
    /// <typeparam name="T">Target type to deserialize each entry into.</typeparam>
    /// <param name="outboxEntries">The outbox rows to map.</param>
    /// <returns>Materialized read-only list of <typeparamref name="T"/> values.</returns>
    /// <exception cref="JsonException">Thrown when any entry's JSON payload cannot be deserialized.</exception>
    public static IReadOnlyList<T> MapTo<T>(this IEnumerable<OutboxEntry> outboxEntries) =>
        outboxEntries
            .Select(e => e.MapTo<T>())
            .ToList();
}
