using Stratara.Abstractions.Entities;

namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Periodic global checkpoint linking the per-stream hash chain into a tamper-evident
/// sequence across every stream of a tenant. Persisted in the <c>event_chain_anchor</c>
/// table; written by the event-stream-hash worker once enough new events have accrued.
/// </summary>
/// <remarks>
/// An anchor exists to be committed to a source of truth <c>outside</c> the application's
/// own infrastructure. A self-contained hash chain only proves the chain is internally
/// consistent — an insider with full database access can recompute every hash after
/// tampering. Publishing an anchor to an external chain, notary, or timestamp authority and
/// recording the reference on <see cref="BlockchainTxHash"/> closes that gap: the externally
/// committed hash cannot be rewritten even by an attacker who controls the database.
/// Stratara records the anchor; choosing and wiring the external service is the consumer's
/// integration point.
/// </remarks>
public sealed class EventChainAnchor : IEntity, IMultiTenant, IBucket, IHasRowVersion
{
    /// <summary>Sequence number this anchor covers up to (inclusive).</summary>
    public long SequenceNumber { get; set; }

    /// <summary>The anchor hash — the head of the hash chain at <see cref="SequenceNumber"/>.</summary>
    public byte[] AnchorHash { get; set; } = [];

    /// <summary>When the anchor was written.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// Optional reference returned by the external source of truth (e.g. a blockchain
    /// transaction hash, notary receipt, or timestamp-authority proof) once this anchor's
    /// <see cref="AnchorHash"/> has been committed outside the application's infrastructure.
    /// <see langword="null"/> until external anchoring is wired up.
    /// </summary>
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
