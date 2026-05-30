namespace Stratara.Sample.TamperProof.EventStore;

// Verifies the local chain against an anchor that was published to an external source of
// truth. Where ChainVerifier only proves the chain is internally consistent, this catches
// a full re-chain: an attacker can rewrite every local hash, but cannot change the hash
// already committed externally. This is the escalation the per-stream chain alone can't make.
public static class AnchorVerifier
{
    public static void VerifyAgainstExternalAnchor(HashChainedEventStore store, ExternalAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(anchor);

        var localHash = store.HashAt(anchor.Sequence);
        if (!localHash.AsSpan().SequenceEqual(anchor.AnchorHash))
        {
            throw new EventStreamCorruptedException(
                anchor.Sequence,
                $"local chain head at #{anchor.Sequence} no longer matches the hash anchored externally " +
                $"({anchor.ExternalTxRef}) — the chain was rewritten after the anchor was published");
        }
    }
}
