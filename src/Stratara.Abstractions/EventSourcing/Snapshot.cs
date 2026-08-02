using Stratara.Abstractions.Entities;
using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Aggregate snapshot — periodic state capture that lets the aggregation service skip
/// replaying every event from the start of a stream.
/// </summary>
/// <remarks>
/// <see cref="TenantId"/> is the Subject (data owner) of the aggregate — same value as
/// <see cref="EventStreamEntry.TenantId"/> for the underlying stream.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class Snapshot : IEntity, IMultiTenant, IBucket, IHasRowVersion
{
    /// <summary>Id of the stream the snapshot captures.</summary>
    public Guid StreamId { get; set; }

    /// <summary>Stream version the snapshot reflects (inclusive).</summary>
    public long Version { get; set; }

    /// <summary>
    /// Assembly-qualified type name of the owning aggregate, as produced when the snapshot is
    /// written. It carries the assembly version, so reads must match it version-independently
    /// (see <see cref="ISnapshotRepository.GetAsync(System.Guid, string, long?, System.Threading.CancellationToken)"/>).
    /// </summary>
    public required string AggregateTypeName { get; set; }

    /// <summary>Serialised aggregate state as JSON.</summary>
    public required string DataJson { get; set; }

    /// <summary>When the snapshot was written.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <inheritdoc/>
    public required int BucketId { get; set; }

    /// <inheritdoc/>
    public Guid Id { get; set; }

    /// <inheritdoc/>
    public uint RowVersion { get; set; }

    /// <inheritdoc/>
    public required Guid TenantId { get; set; }
}
