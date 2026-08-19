# Hero Sample — TamperProof

> **Derived page.** The behaviour described here is specified by the `tamper-evident-streams` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

**Concept**: Hash-chained event streams catch direct-DB tampering at the next verification pass. The full *why* lives in the [Tamper-Evident Streams](../concepts/tamper-evident-streams.md) concept page; this is the runnable proof.

- **Code**: [`samples/Stratara.Sample.TamperProof`](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.TamperProof)
- **Lines**: ~320
- **Read time**: 5–10 min
- **Dependencies**: none — pure in-memory, no database, no DI container.

## What you'll see

1. **`HashChainedEventStore`** — every `Append` computes `SHA-256(previousHash || canonical-json(payload))` and stores both the entry's own hash and a pointer to the previous entry's hash.
2. **Three events appended** — `AccountOpened`, `AmountDeposited`, `AmountWithdrawn`. The console prints each entry with a six-byte hash preview so the chain is visually inspectable.
3. **`ChainVerifier.Verify`** — the clean run passes. Every stored hash matches a fresh re-hash; every previous-hash pointer matches the prior entry's hash.
4. **`TamperWithPayloadForDemo`** — simulates an attacker editing entry #2 directly in the database, rewriting `$50` to `$5000`. The stored hash is untouched.
5. **`ChainVerifier.Verify` again** — raises `EventStreamCorruptedException` at sequence #2 with a precise reason: *"stored hash does not match a fresh re-hash of the payload (payload was modified after commit)"*.

## Running

```bash
dotnet run --project samples/Stratara.Sample.TamperProof
```

Expected output (abridged):

```
=== Stratara TamperProof ===

--- Append three events ---
  #1  AccountOpened         hash=70be4f…
  #2  AmountDeposited       hash=796018…
  #3  AmountWithdrawn       hash=6a0260…

--- Verify the chain (clean) ---
  OK — every entry's stored hash matches a fresh re-hash of its payload.
  OK — every entry's previous-hash pointer matches the prior entry's hash.

--- Tamper: rewrite entry #2's deposit from $50 to $5000 ---

--- Verify the chain (tampered) ---
  CAUGHT: Event stream tampering detected at sequence #2: stored hash does not match a fresh re-hash of the payload (payload was modified after commit)
```

## How this maps to the real Stratara

| Sample | Real Stratara |
|---|---|
| `HashChainedEventStore` (in-memory list) | Hash columns on `event_stream_entry` populated by EF Core |
| `HashChainedEventStore.Append` computing the hash inline | The `EventStreamHashing` worker (`AddEventStreamHashWorkerServices()`) — a background service that hashes each newly-committed event a beat behind the write, in batches, so appends stay cheap |
| `ChainVerifier.Verify` (on-demand) | **No framework equivalent — this stays your job.** Stratara writes the chain and the anchors; recomputing them to look for a break is a deliberate pass you schedule (an audit job, or the external-anchor check). Nothing in the framework scans for tampering on its own |
| `TamperWithPayloadForDemo` | No equivalent — production code has no public mutation path on persisted entries |

## Sister hero sample

- **[Hero Sample — Encryption](hero-encryption.md)** — the confidentiality counterpart. Same goal of integrity-by-construction, applied at the field level instead of the stream level.
