using System.Globalization;
using Stratara.Sample.TamperProof.Domain;
using Stratara.Sample.TamperProof.EventStore;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");

Console.WriteLine("=== Stratara TamperProof ===");
Console.WriteLine();
Console.WriteLine("Stratara's EventStreamHashing worker chains every event into the next, so any");
Console.WriteLine("post-commit edit of a row in Postgres is caught at the next verification pass.");
Console.WriteLine("This sample is the same idea in 100 lines of in-memory code.");
Console.WriteLine();

var store = new HashChainedEventStore();
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

Console.WriteLine("--- Tamper: rewrite entry #2's deposit from $50 to $5000 ---");
store.TamperWithPayloadForDemo(1, new AmountDeposited(accountId, 5000m, now.AddMinutes(1)));
Console.WriteLine("  Done. The malicious row sits in the store. Aggregate replay would now");
Console.WriteLine("  silently produce a wrong balance. But the hash chain still has the old hash.");
Console.WriteLine();

Console.WriteLine("--- Verify the chain (tampered) ---");
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

Console.WriteLine("In production this verification runs as a background worker (Stratara.EventSourcing.WorkerDefaults");
Console.WriteLine("composite 'EventStreamHashing'). A failure raises an alert through OpenTelemetry");
Console.WriteLine("and halts further projection processing until an operator confirms the audit fix.");
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
