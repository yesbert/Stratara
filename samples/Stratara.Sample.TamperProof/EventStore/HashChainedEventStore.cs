using System.Security.Cryptography;
using System.Text.Json;

namespace Stratara.Sample.TamperProof.EventStore;

// A minimal in-memory model of the hash-chain Stratara's EventStreamHashing
// worker maintains against PostgreSQL. Each appended event is hashed together
// with the previous entry's hash, so any after-the-fact mutation of an event
// payload breaks the chain at the next verification pass.
public sealed class HashChainedEventStore
{
    private static readonly byte[] GenesisHash = new byte[32];
    private readonly List<HashChainedEntry> _entries = [];

    public IReadOnlyList<HashChainedEntry> Entries => _entries;

    public HashChainedEntry Append(object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var previousHash = _entries.Count == 0 ? GenesisHash : _entries[^1].Hash;
        var hash = ComputeHash(previousHash, payload);
        var entry = new HashChainedEntry(_entries.Count + 1, payload, previousHash, hash);
        _entries.Add(entry);
        return entry;
    }

    // Demo-only: simulates an attacker / admin editing a row directly in the
    // database. Real Stratara has no public API for this — that's the point.
    public void TamperWithPayloadForDemo(int index, object newPayload)
    {
        if (index < 0 || index >= _entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        _entries[index].OverwritePayloadForTamperDemo(newPayload);
    }

    internal static byte[] ComputeHash(byte[] previousHash, object payload)
    {
        var payloadJson = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType());
        var combined = new byte[previousHash.Length + payloadJson.Length];
        Buffer.BlockCopy(previousHash, 0, combined, 0, previousHash.Length);
        Buffer.BlockCopy(payloadJson, 0, combined, previousHash.Length, payloadJson.Length);
        return SHA256.HashData(combined);
    }
}
