namespace Stratara.Sample.TamperProof.EventStore;

// Simulates a source of truth OUTSIDE our own infrastructure — a public blockchain, a
// notary, an RFC 3161 timestamp authority, an OpenTimestamps calendar. The defining
// property: it is append-only and we cannot rewrite it after the fact, even with full
// control of our own database. In real Stratara this is what the EventChainAnchor's
// BlockchainTxHash column points at — the framework records the anchor; you choose and
// wire the external service it is committed to.
public sealed class ExternalAnchorLog
{
    private readonly List<ExternalAnchor> _published = [];

    public IReadOnlyList<ExternalAnchor> Published => _published;

    public ExternalAnchor Publish(long sequence, byte[] anchorHash)
    {
        ArgumentNullException.ThrowIfNull(anchorHash);

        // The transaction reference the external system hands back. Faked here; in
        // production this is the blockchain tx hash / notary receipt / OTS proof that
        // would be stored on EventChainAnchor.BlockchainTxHash.
        var externalTxRef = "ext:" + Convert.ToHexStringLower(anchorHash.AsSpan(0, 6));
        var anchor = new ExternalAnchor(sequence, anchorHash, externalTxRef);
        _published.Add(anchor);
        return anchor;
    }
}

public sealed record ExternalAnchor(long Sequence, byte[] AnchorHash, string ExternalTxRef);
