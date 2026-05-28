using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Stratara.Contracts.Messages;

/// <summary>
/// Wire-level envelope for a command in transit through the outbox / message bus. Carries the serialized
/// command payload plus the metadata needed to re-resolve its CLR type and propagate the session context.
/// </summary>
/// <remarks>
/// Property names are pinned via <see cref="JsonPropertyNameAttribute"/> so the wire format is independent of
/// any consumer-side <c>JsonSerializerOptions.PropertyNamingPolicy</c>.
/// </remarks>
/// <param name="Id">Stable envelope id (also used as the outbox row id).</param>
/// <param name="CommandJson">JSON serialization of the command's runtime instance.</param>
/// <param name="CommandTypeName">Version-independent type name used by the worker-side dispatcher to resolve the concrete command type.</param>
/// <param name="SessionContextJson">JSON serialization of the originating <see cref="Session.SessionContext"/> for handler-side replay.</param>
/// <param name="Signature">Optional opaque signature produced by <c>IBusEnvelopeSigner</c> when bus integrity is enabled. Defaults to <c>null</c> for backwards compatibility with hosts that have not opted in.</param>
[ExcludeFromCodeCoverage]
public sealed record CommandEnvelope(
    [property: JsonPropertyName("Id")] Guid Id,
    [property: JsonPropertyName("CommandJson")] string CommandJson,
    [property: JsonPropertyName("CommandTypeName")] string CommandTypeName,
    [property: JsonPropertyName("SessionContextJson")] string SessionContextJson,
    [property: JsonPropertyName("Signature")] string? Signature = null);
