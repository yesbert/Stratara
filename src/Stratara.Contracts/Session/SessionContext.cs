using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Stratara.Contracts.Session;

/// <summary>
/// Dual-identity session context: Actor (who triggered) + the data-owner pair
/// (<see cref="TenantId"/>, <see cref="UserId"/>). For 95% of operations Actor
/// equals the data-owner; the distinction only diverges for PlatformAdmin
/// cross-tenant operations, anonymous endpoints, and system/saga flows.
///
/// Convention across the codebase: unprefixed properties (<c>TenantId</c>,
/// <c>UserId</c>) identify the data owner; <c>Actor*</c> properties identify
/// the principal who triggered the write (audit / "who did this"). This matches
/// every other <c>IMultiTenant</c> entity in the system.
/// </summary>
/// <param name="CorrelationId">Stable correlation identifier (typically a v7 GUID string) — propagated across hops for log/trace correlation.</param>
/// <param name="CausationId">Causation identifier (the upstream command/event id) — propagated for causality chains.</param>
/// <param name="ClientConnectionId">Optional connection identifier (SignalR / WebSocket / call session) — used for proactive push.</param>
/// <param name="ActorTenantId">Tenant id of the principal who triggered the operation (audit dimension).</param>
/// <param name="ActorUserId">User id of the principal who triggered the operation (audit dimension).</param>
/// <param name="TenantId">Tenant the data belongs to (data-owner dimension; routing, encryption AAD, query filter).</param>
/// <param name="UserId">Optional user the data belongs to (null for tenant-scoped aggregates).</param>
/// <param name="ClientId">Optional client identifier (browser tab / phone call / SSR session) — used as Conversation/ProactiveSession stream id.</param>
[ExcludeFromCodeCoverage]
public sealed record SessionContext(
    [property: JsonPropertyName("CorrelationId")] string CorrelationId,
    [property: JsonPropertyName("CausationId")] string? CausationId,
    [property: JsonPropertyName("ClientConnectionId")] string? ClientConnectionId,
    [property: JsonPropertyName("ActorTenantId")] Guid ActorTenantId,
    [property: JsonPropertyName("ActorUserId")] Guid ActorUserId,
    [property: JsonPropertyName("TenantId")] Guid TenantId,
    [property: JsonPropertyName("UserId")] Guid? UserId,
    [property: JsonPropertyName("ClientId")] Guid? ClientId = null)
{
    /// <summary>
    /// Sentinel for service/saga flows that have no inherited Actor.
    /// Distinguishable from Guid.Empty (which is used for anonymous endpoints).
    /// </summary>
    public static readonly Guid SystemActorTenantId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>
    /// Sentinel for service/saga flows that have no inherited Actor.
    /// Distinguishable from Guid.Empty (which is used for anonymous endpoints).
    /// </summary>
    public static readonly Guid SystemActorUserId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>Returns an empty/anonymous session-context value with freshly generated correlation + causation ids.</summary>
    public static SessionContext Empty() =>
        new(
            Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7().ToString("N"),
            null,
            Guid.Empty,
            Guid.Empty,
            Guid.Empty,
            null,
            null
        );
}
