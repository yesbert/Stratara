using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Stratara.Contracts.Messages;

/// <summary>
/// Wire-level bundle of events emitted by a single command. Travels through the outbox + message bus as
/// one unit so projections and sagas see a transactionally-consistent slice.
/// </summary>
/// <remarks>
/// Property names are pinned via <see cref="JsonPropertyNameAttribute"/> so the wire format is independent of
/// any consumer-side <c>JsonSerializerOptions.PropertyNamingPolicy</c>.
/// </remarks>
/// <param name="Events">Ordered list of events in the bundle.</param>
/// <param name="SessionContextJson">JSON serialization of the originating <see cref="Session.SessionContext"/>.</param>
/// <param name="Signature">Optional opaque signature produced by <c>IBusEnvelopeSigner</c> when bus integrity is enabled. Defaults to <c>null</c> for backwards compatibility with hosts that have not opted in.</param>
[ExcludeFromCodeCoverage]
public sealed record EventBundle(
    [property: JsonPropertyName("Events")] IReadOnlyList<EventMessage> Events,
    [property: JsonPropertyName("SessionContextJson")] string SessionContextJson,
    [property: JsonPropertyName("Signature")] string? Signature = null);
