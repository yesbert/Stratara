using Stratara.Abstractions.Entities;
using System.Diagnostics.CodeAnalysis;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing.Entities;

/// <summary>
/// Command audit row persisted to <c>command_log_entry</c>. Captures the serialized command
/// payload, its routing metadata, and the dual identity pair used for tenant-scoped audit:
/// <see cref="TenantId"/> + <see cref="UserId"/> identify the data owner;
/// <see cref="ActorTenantId"/> + <see cref="ActorUserId"/> identify who issued the command
/// (the two may differ for PlatformAdmin cross-tenant flows).
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class CommandLogEntry : IEntity, IMultiTenant, IBucket, IHasRowVersion
{
    /// <summary>Optional causation id linking this command to the event that triggered it.</summary>
    public string? CausationId { get; set; }

    /// <summary>Correlation id of the originating request, propagated across the saga / outbox chain.</summary>
    public required string CorrelationId { get; set; }

    /// <summary>JSON-serialized command payload (encrypted via <c>ISecureJsonSerializer</c>).</summary>
    public required string CommandJson { get; set; }

    /// <summary>Qualified type name of the command, used to deserialize and route.</summary>
    public required string CommandTypeName { get; set; }

    /// <summary>UTC timestamp the command was logged.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Bucket partition id used to shard outbox / hashing work.</summary>
    public required int BucketId { get; set; }

    /// <summary>Primary key — typically a GUID v7 generated at command-log insert time.</summary>
    public Guid Id { get; set; }

    /// <summary>Row-version concurrency token managed by the row-version convention.</summary>
    public uint RowVersion { get; set; }

    /// <summary>Tenant id of the data owner. Uniform with <c>IMultiTenant.TenantId</c>.</summary>
    public required Guid TenantId { get; set; }

    /// <summary>User id of the data owner (optional — system-issued commands have none).</summary>
    public Guid? UserId { get; set; }

    /// <summary>Actor tenant id — the tenant context the issuer was acting under.</summary>
    public required Guid ActorTenantId { get; set; }

    /// <summary>Actor user id — the user who issued the command (audit trail).</summary>
    public required Guid ActorUserId { get; set; }
}
