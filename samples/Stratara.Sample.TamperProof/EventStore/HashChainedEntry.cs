namespace Stratara.Sample.TamperProof.EventStore;

public sealed class HashChainedEntry
{
    public HashChainedEntry(long sequence, object payload, byte[] previousHash, byte[] hash)
    {
        Sequence = sequence;
        Payload = payload;
        PreviousHash = previousHash;
        Hash = hash;
    }

    public long Sequence { get; }

    public object Payload { get; private set; }

    public byte[] PreviousHash { get; private set; }

    public byte[] Hash { get; private set; }

    internal void OverwritePayloadForTamperDemo(object newPayload) => Payload = newPayload;

    // Demo-only: an attacker with full database write access recomputes this entry's
    // hash pointers so the local chain stays internally consistent after a tamper.
    internal void RechainForDemo(byte[] previousHash, byte[] hash)
    {
        PreviousHash = previousHash;
        Hash = hash;
    }
}
