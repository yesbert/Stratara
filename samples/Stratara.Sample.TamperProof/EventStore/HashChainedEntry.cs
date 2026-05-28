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

    public byte[] PreviousHash { get; }

    public byte[] Hash { get; }

    internal void OverwritePayloadForTamperDemo(object newPayload) => Payload = newPayload;
}
