# Stratara.Sample.TamperProof

**One of two Why-Stratara hero samples.** Shows the hash-chain audit trick that Stratara's `EventStreamHashing` worker performs against PostgreSQL — distilled to ~100 lines of in-memory code so the *idea* is visible without a database.

## The pitch

Append-only event stores have an obvious blind spot: **what if someone edits a row directly in the database?** A normal aggregate replay won't notice. The next projection rebuild would happily produce a wrong balance from the wrong event.

Stratara chains every event into the next via SHA-256. Any after-the-fact mutation of a payload — by a malicious admin, a buggy migration, or a forgotten `UPDATE` from the database console — breaks the chain, and any verification pass over it surfaces the break at the exact row.

But a self-contained chain has a limit: an insider with full database access can recompute *every* hash after tampering, leaving the chain internally consistent. Owning both ends of the chain cuts both ways. That's what **anchors** are for — periodic checkpoints you commit to a source of truth *outside* your infrastructure (a public chain, a notary, a timestamp authority), so a full re-chain still can't match the externally committed hash. This sample shows both: the chain catching a naive tamper, and an external anchor catching a full re-chain the chain alone cannot.

## What to look at, in order

1. **`EventStore/HashChainedEntry.cs`** — the per-event record. Carries the payload, the previous-entry hash, and the entry's own hash. Hashes are byte arrays, not strings, so there's no normalization weirdness.

2. **`EventStore/HashChainedEventStore.cs`** — `Append` computes `SHA256(previousHash || canonical-json(payload))` and stores both the hash and the previous-hash pointer. `TamperWithPayloadForDemo` simulates an attacker editing a row — it replaces the payload **without** updating the hash.

3. **`EventStore/ChainVerifier.cs`** — the verifier walks the entries, recomputes each hash from `previousHash || payload`, and compares both the recomputed hash *and* the previous-hash pointer. Either mismatch raises `EventStreamCorruptedException` with the offending sequence number.

4. **`EventStore/ExternalAnchorLog.cs`** — a stand-in for a source of truth *outside* your infrastructure (a public blockchain, a notary, an OpenTimestamps calendar). Append-only; `Publish` returns an `ExternalAnchor` whose `ExternalTxRef` mirrors the real `EventChainAnchor.BlockchainTxHash`.

5. **`EventStore/AnchorVerifier.cs`** — checks the current chain head against an externally published anchor. Where `ChainVerifier` only proves internal consistency, this catches a *full re-chain*.

6. **`Program.cs`** — the demo script, in three acts:
   - Append three events; verify clean; **publish an anchor over the clean head to the external log**.
   - Tamper with entry #2 (`$50` → `$5000`), leaving a stale hash — caught by `ChainVerifier` at #2.
   - **Re-chain the whole store** (the insider recomputes every hash) — now `ChainVerifier` *passes*, but `AnchorVerifier` catches the mismatch against the external anchor at #3.

## Run it

```bash
dotnet run --project samples/Stratara.Sample.TamperProof
```

Expected: the clean verify passes; the naive tamper is caught at sequence #2; after a full re-chain the local verify passes again but the external-anchor check is caught at sequence #3 (*"local chain head … no longer matches the hash anchored externally"*).

## How this maps to the real Stratara

| Sample | Real Stratara |
|---|---|
| `HashChainedEventStore` (in-memory) | Hash columns on `event_stream_entry` populated by EF Core, computed against the same algorithm |
| `ChainVerifier` (sync, on demand) | The same on-demand recomputation you run against the real chain — an audit job, or the external-anchor check. The `EventStreamHashing` worker *builds* the chain (forward-hashing newly-committed events, with structured logs); detecting a break is this verification, run deliberately, not by the worker itself. |
| `ExternalAnchorLog` / `ExternalAnchor` | The `event_chain_anchor` table + `EventChainAnchor` row; `ExternalTxRef` ↔ the `BlockchainTxHash` column. The worker writes anchors; committing them to a real external source of truth is the consumer's integration point. |
| `AnchorVerifier` | The (consumer-wired) re-verification of an anchor against the external source of truth it was committed to. |
| `TamperWithPayloadForDemo` / `RechainEntireStoreForDemo` | No equivalent — production code has no public mutation path on persisted entries. The threat model is *direct DB access* (including a full re-chain by an insider), not framework-mediated mutation. |

## Concept doc

For the wider *why* — threat model, what we hash, why SHA-256, how the worker schedules itself — see [Tamper-Evident Streams](https://docs.stratara.tech/concepts/tamper-evident-streams.html).

## Sister hero sample

- **[`Stratara.Sample.Encryption`](../Stratara.Sample.Encryption)** — the other side of the integrity story: confidentiality. Tenant-bound AES-GCM so a leaked dump from one tenant cannot be decrypted with another tenant's keys.
