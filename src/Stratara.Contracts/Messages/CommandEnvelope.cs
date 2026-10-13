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
/// <param name="Heavy">
/// Whether the command declared itself long-running when it was enqueued. Recorded at enqueue time so
/// republication from durable storage can route the envelope to the heavy lane without re-resolving the
/// command's CLR type. Defaults to <c>false</c>, which is how an envelope written before this field
/// existed deserializes.
/// </param>
[ExcludeFromCodeCoverage]
public sealed record CommandEnvelope(
    [property: JsonPropertyName("Id")] Guid Id,
    [property: JsonPropertyName("CommandJson")] string CommandJson,
    [property: JsonPropertyName("CommandTypeName")] string CommandTypeName,
    [property: JsonPropertyName("SessionContextJson")] string SessionContextJson,
    [property: JsonPropertyName("Signature")] string? Signature = null,
    [property: JsonPropertyName("Heavy")] bool Heavy = false)
{
    /// <summary>
    /// The tenant the command's protected fields were encrypted for, when the command named its data
    /// owner; <see langword="null"/> when it named none and its fields were encrypted for the session.
    /// Omitted from the serialized form when <see langword="null"/>, which is also how an envelope
    /// written before this property existed deserializes.
    /// </summary>
    [JsonPropertyName("DataOwnerTenantId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DataOwnerTenantId { get; init; }

    /// <summary>
    /// The user the command's protected fields were encrypted for, when the command named one beside
    /// its data-owner tenant; <see langword="null"/> otherwise. Read only where
    /// <see cref="DataOwnerTenantId"/> is set, and omitted from the serialized form when
    /// <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("DataOwnerUserId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DataOwnerUserId { get; init; }
}
