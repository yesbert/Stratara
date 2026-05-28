namespace Stratara.Sample.TamperProof.EventStore;

public sealed class EventStreamCorruptedException : Exception
{
    public EventStreamCorruptedException(long sequence, string reason)
        : base($"Event stream tampering detected at sequence #{sequence}: {reason}")
    {
        Sequence = sequence;
    }

    public long Sequence { get; }
}

public static class ChainVerifier
{
    public static void Verify(HashChainedEventStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        byte[] previousHash = new byte[32];
        foreach (var entry in store.Entries)
        {
            if (!ByteArraysEqual(previousHash, entry.PreviousHash))
            {
                throw new EventStreamCorruptedException(
                    entry.Sequence,
                    "previous-hash pointer does not match the prior entry's hash");
            }
            var recomputed = HashChainedEventStore.ComputeHash(previousHash, entry.Payload);
            if (!ByteArraysEqual(recomputed, entry.Hash))
            {
                throw new EventStreamCorruptedException(
                    entry.Sequence,
                    "stored hash does not match a fresh re-hash of the payload (payload was modified after commit)");
            }
            previousHash = entry.Hash;
        }
    }

    private static bool ByteArraysEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }
        return true;
    }
}
