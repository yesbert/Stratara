using Stratara.Abstractions.Entities;
using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Event stream entry persisted in the write store. One row per appended event.
/// </summary>
/// <remarks>
/// Identity convention: <see cref="TenantId"/> + <see cref="UserId"/> identify the
/// data owner (Subject — uniform with <see cref="IMultiTenant.TenantId"/> on every
/// other entity); <see cref="ActorTenantId"/> + <see cref="ActorUserId"/> identify the
/// principal who triggered the write (Actor — audit trail). They differ only for
/// PlatformAdmin cross-tenant operations and system / saga flows.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class EventStreamEntry : IEntity, IMultiTenant, IBucket, IHasRowVersion
{
    /// <summary>Global sequence number — monotonically increasing across all streams.</summary>
    public long SequenceNumber { get; set; }

    /// <summary>Id of the stream the entry belongs to (= aggregate id).</summary>
    public required Guid StreamId { get; set; }

    /// <summary>Aggregate-relative version. <c>1</c> is the creation event.</summary>
    public required long Version { get; set; }

    /// <summary>Fully-qualified, version-independent type name of the event payload.</summary>
    public required string EventTypeName { get; set; }

    /// <summary>Fully-qualified, version-independent type name of the owning aggregate.</summary>
    public required string AggregateTypeName { get; set; }

    /// <summary>Serialised event payload as JSON.</summary>
    public required string DataJson { get; set; }

    /// <summary>When the event was appended.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Cross-service correlation id propagated from the originating request.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Id of the command that caused this event — set by the command-audit pipeline behaviour.</summary>
    public string? CausationId { get; set; }

    /// <summary>Hash of the preceding stream entry; <c>null</c> until the hash worker fills it in.</summary>
    public byte[]? PreviousHash { get; set; }

    /// <summary>Hash of this entry; <c>null</c> until the hash worker fills it in.</summary>
    public byte[]? Hash { get; set; }

    /// <inheritdoc/>
    public required int BucketId { get; set; }

    /// <inheritdoc/>
    public Guid Id { get; set; }

    /// <inheritdoc/>
    public uint RowVersion { get; set; }

    /// <summary>Subject (data-owner) tenant id — uniform with <see cref="IMultiTenant.TenantId"/>.</summary>
    public required Guid TenantId { get; set; }

    /// <summary>Subject (data-owner) user id — only meaningful for user-aggregates; <c>null</c> otherwise.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Actor — the tenant that triggered the write (PlatformAdmin's home tenant in cross-tenant flows).</summary>
    public required Guid ActorTenantId { get; set; }

    /// <summary>Actor — the user that triggered the write.</summary>
    public required Guid ActorUserId { get; set; }
}
