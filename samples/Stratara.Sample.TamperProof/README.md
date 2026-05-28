# Stratara.Sample.TamperProof

**One of two Why-Stratara hero samples.** Shows the hash-chain audit trick that Stratara's `EventStreamHashing` worker performs against PostgreSQL — distilled to ~100 lines of in-memory code so the *idea* is visible without a database.

## The pitch

Append-only event stores have an obvious blind spot: **what if someone edits a row directly in the database?** A normal aggregate replay won't notice. The next projection rebuild would happily produce a wrong balance from the wrong event.

Stratara chains every event into the next via SHA-256. Any after-the-fact mutation of a payload — by a malicious admin, a buggy migration, or a forgotten `UPDATE` from the database console — breaks the chain at the next verification pass. It's blockchain-style integrity without the consensus overhead, because we own both ends of the chain.

## What to look at, in order

1. **`EventStore/HashChainedEntry.cs`** — the per-event record. Carries the payload, the previous-entry hash, and the entry's own hash. Hashes are byte arrays, not strings, so there's no normalization weirdness.

2. **`EventStore/HashChainedEventStore.cs`** — `Append` computes `SHA256(previousHash || canonical-json(payload))` and stores both the hash and the previous-hash pointer. `TamperWithPayloadForDemo` simulates an attacker editing a row — it replaces the payload **without** updating the hash.

3. **`EventStore/ChainVerifier.cs`** — the verifier walks the entries, recomputes each hash from `previousHash || payload`, and compares both the recomputed hash *and* the previous-hash pointer. Either mismatch raises `EventStreamCorruptedException` with the offending sequence number.

4. **`Program.cs`** — the demo script:
   - Append three events. Print the chain.
   - Verify — clean run.
   - Tamper with entry #2 (rewrite `$50` deposit to `$5000`).
   - Verify again — caught at sequence #2 with a precise reason.

## Run it

```bash
dotnet run --project samples/Stratara.Sample.TamperProof
```

Expected: the clean verify passes, the tampered verify raises `EventStreamCorruptedException` at sequence #2 with the reason *"stored hash does not match a fresh re-hash of the payload (payload was modified after commit)"*.

## How this maps to the real Stratara

| Sample | Real Stratara |
|---|---|
| `HashChainedEventStore` (in-memory) | Hash columns on `event_stream_entry` populated by EF Core, computed against the same algorithm |
| `ChainVerifier` (sync, on demand) | `EventStreamHashingWorker` from the `Stratara.EventSourcing.WorkerDefaults` composite — runs as a background service, processes batches, surfaces breaks via OpenTelemetry counters + structured logs |
| `TamperWithPayloadForDemo` | No equivalent — production code has no public mutation path on persisted entries. The threat model is *direct DB access*, not framework-mediated mutation. |

## Concept doc

For the wider *why* — threat model, what we hash, why SHA-256, how the worker schedules itself — see [Tamper-Evident Streams](https://docs.stratara.tech/concepts/tamper-evident-streams.html).

## Sister hero sample

- **[`Stratara.Sample.Encryption`](../Stratara.Sample.Encryption)** — the other side of the integrity story: confidentiality. Tenant-bound AES-GCM so a leaked dump from one tenant cannot be decrypted with another tenant's keys.
