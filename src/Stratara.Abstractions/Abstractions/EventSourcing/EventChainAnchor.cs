using Stratara.Abstractions.Entities;

namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Periodic global hash linking the per-stream hash chain into a tamper-evident
/// sequence across every stream of a tenant. Persisted in the
/// <c>event_chain_anchor</c> table; written by the event-stream-hash worker.
/// </summary>
public sealed class EventChainAnchor : IEntity, IMultiTenant, IBucket, IHasRowVersion
{
    /// <summary>Sequence number this anchor covers up to (inclusive).</summary>
    public long SequenceNumber { get; set; }

    /// <summary>The anchor hash — typically SHA-256 of the previous anchor + every covered event hash.</summary>
    public byte[] AnchorHash { get; set; } = [];

    /// <summary>When the anchor was written.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Optional external transaction hash if the anchor was also committed to an external chain.</summary>
    public string? BlockchainTxHash { get; set; }

    /// <inheritdoc/>
    public required int BucketId { get; set; }

    /// <inheritdoc/>
    public Guid Id { get; set; }

    /// <inheritdoc/>
    public uint RowVersion { get; set; }

    /// <inheritdoc/>
    public required Guid TenantId { get; set; }
}
