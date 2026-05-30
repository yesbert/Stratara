using System.Globalization;
using Stratara.Sample.TamperProof.Domain;
using Stratara.Sample.TamperProof.EventStore;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");

Console.WriteLine("=== Stratara TamperProof ===");
Console.WriteLine();
Console.WriteLine("Stratara's EventStreamHashing worker chains every event into the next, so any");
Console.WriteLine("post-commit edit of a row in Postgres is caught at the next verification pass.");
Console.WriteLine("This sample is that idea — plus the external-anchor escalation — in in-memory code.");
Console.WriteLine();

var store = new HashChainedEventStore();
var external = new ExternalAnchorLog();
var accountId = Guid.NewGuid();
var now = DateTimeOffset.UtcNow;

Console.WriteLine("--- Append three events ---");
store.Append(new AccountOpened(accountId, "Alice", 100m, now));
store.Append(new AmountDeposited(accountId, 50m, now.AddMinutes(1)));
store.Append(new AmountWithdrawn(accountId, 30m, now.AddMinutes(2)));
PrintChain(store);

Console.WriteLine("--- Verify the chain (clean) ---");
ChainVerifier.Verify(store);
Console.WriteLine("  OK — every entry's stored hash matches a fresh re-hash of its payload.");
Console.WriteLine("  OK — every entry's previous-hash pointer matches the prior entry's hash.");
Console.WriteLine();

Console.WriteLine("--- Anchor the clean chain to an external source of truth ---");
var anchor = external.Publish(3, store.HashAt(3));
Console.WriteLine($"  Published anchor over #{anchor.Sequence} to an external log (ref {anchor.ExternalTxRef}).");
Console.WriteLine("  In real Stratara this is the EventChainAnchor row; its BlockchainTxHash column");
Console.WriteLine("  holds the reference returned by a blockchain / notary / timestamp service.");
Console.WriteLine();

Console.WriteLine("--- Tamper: rewrite entry #2's deposit from $50 to $5000 ---");
store.TamperWithPayloadForDemo(1, new AmountDeposited(accountId, 5000m, now.AddMinutes(1)));
Console.WriteLine("  Done. The malicious row sits in the store with a now-stale hash.");
Console.WriteLine();

Console.WriteLine("--- Verify the chain (tampered, stale hash) ---");
try
{
    ChainVerifier.Verify(store);
    Console.WriteLine("  Verification passed — this should not happen.");
}
catch (EventStreamCorruptedException ex)
{
    Console.WriteLine($"  CAUGHT: {ex.Message}");
}
Console.WriteLine();

Console.WriteLine("--- A determined insider recomputes EVERY hash ---");
Console.WriteLine("  An attacker with full database access wouldn't leave a stale hash. Owning both");
Console.WriteLine("  ends of the chain, they re-hash the entire stream so it is consistent again.");
store.RechainEntireStoreForDemo();
Console.WriteLine();

Console.WriteLine("--- Verify the chain (tampered, fully re-chained) ---");
try
{
    ChainVerifier.Verify(store);
    Console.WriteLine("  PASSES — the local chain is internally consistent again. A self-contained");
    Console.WriteLine("  hash chain is now blind to the $5000 edit. This is its honest limit.");
}
catch (EventStreamCorruptedException ex)
{
    Console.WriteLine($"  CAUGHT: {ex.Message} — should not happen after a full re-chain.");
}
Console.WriteLine();

Console.WriteLine("--- Verify against the external anchor ---");
try
{
    AnchorVerifier.VerifyAgainstExternalAnchor(store, anchor);
    Console.WriteLine("  Anchor matches — this should not happen after tampering.");
}
catch (EventStreamCorruptedException ex)
{
    Console.WriteLine($"  CAUGHT: {ex.Message}");
}
Console.WriteLine();

Console.WriteLine("The external anchor closes the gap a self-contained chain cannot: an insider who");
Console.WriteLine("rewrites every local hash still cannot change the hash already committed outside");
Console.WriteLine("their reach. In Stratara, EventChainAnchor.BlockchainTxHash is the seam for this —");
Console.WriteLine("you wire the anchor service (public chain, notary, OpenTimestamps); the framework");
Console.WriteLine("stays application-agnostic about which one.");
Console.WriteLine();
Console.WriteLine("Done.");

static void PrintChain(HashChainedEventStore store)
{
    foreach (var entry in store.Entries)
    {
        var payloadName = entry.Payload.GetType().Name;
        var hashPreview = Convert.ToHexStringLower(entry.Hash.AsSpan(0, 6));
        Console.WriteLine($"  #{entry.Sequence}  {payloadName,-20}  hash={hashPreview}…");
    }
    Console.WriteLine();
}
