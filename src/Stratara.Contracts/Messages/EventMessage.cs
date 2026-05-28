using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Stratara.Contracts.Messages;

/// <summary>
/// Wire-level representation of a single event that occurred on an aggregate stream. Carries enough
/// metadata for projection / saga workers to reconstruct the event without database access.
/// </summary>
/// <remarks>
/// Property names are pinned via <see cref="JsonPropertyNameAttribute"/> so the wire format is independent of
/// any consumer-side <c>JsonSerializerOptions.PropertyNamingPolicy</c>.
/// </remarks>
/// <param name="Id">Event identifier (typically a sortable v7 GUID).</param>
/// <param name="Version">Aggregate-stream version this event sits at (1-based).</param>
/// <param name="DataJson">JSON serialization of the event's payload.</param>
/// <param name="StreamId">Identifier of the aggregate stream this event belongs to.</param>
/// <param name="EventTypeName">Version-independent type name used to resolve the concrete event type.</param>
/// <param name="AggregateTypeName">Version-independent type name of the aggregate the event was written against.</param>
/// <param name="ActorTenantId">Tenant id of the principal that triggered the write (audit dimension).</param>
/// <param name="ActorUserId">User id of the principal that triggered the write (audit dimension).</param>
/// <param name="TenantId">Tenant the data belongs to (data-owner dimension; used for routing and AAD).</param>
/// <param name="UserId">Optional user the data belongs to (null for tenant-scoped aggregates).</param>
[ExcludeFromCodeCoverage]
public sealed record EventMessage(
    [property: JsonPropertyName("Id")] Guid Id,
    [property: JsonPropertyName("Version")] long Version,
    [property: JsonPropertyName("DataJson")] string DataJson,
    [property: JsonPropertyName("StreamId")] Guid StreamId,
    [property: JsonPropertyName("EventTypeName")] string EventTypeName,
    [property: JsonPropertyName("AggregateTypeName")] string AggregateTypeName,
    [property: JsonPropertyName("ActorTenantId")] Guid ActorTenantId,
    [property: JsonPropertyName("ActorUserId")] Guid ActorUserId,
    [property: JsonPropertyName("TenantId")] Guid TenantId,
    [property: JsonPropertyName("UserId")] Guid? UserId);
