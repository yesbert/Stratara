# Tamper-Evident Streams

## The problem

Append-only event stores have an obvious blind spot. The events themselves are immutable *by convention* — domain code never overwrites a row. But the rows are still rows in a database. A malicious admin, a buggy migration, an ad-hoc `UPDATE` through the wrong session, a restored backup that swaps in different bytes — and the immutability promise is gone.

A normal aggregate replay won't notice. Projections happily rebuild from the wrong events. The first sign of trouble is usually a customer noticing their balance is off.

## How Stratara solves it

Every entry in the event stream carries two extra columns: a **hash** of the entry's contents and a **previous-hash** pointer to the prior entry's hash. The hash is `SHA-256(previousHash || canonical-json(payload))`.

```
┌──────────────────────────────────────────────────────────────┐
│  #1  AccountOpened       prev=00000…  hash=70be4f…           │
│       ↑                                  │                   │
│       │                                  ▼                   │
│  #2  AmountDeposited    prev=70be4f…  hash=796018…           │
│       ↑                                  │                   │
│       │                                  ▼                   │
│  #3  AmountWithdrawn    prev=796018…  hash=6a0260…           │
└──────────────────────────────────────────────────────────────┘
```

A separate background worker (`EventStreamHashing`, one of the composites in `Stratara.EventSourcing.WorkerDefaults`) walks each stream periodically and re-computes the chain. Any divergence — payload mutated, hash column rewritten, an entry removed in the middle — raises `EventStreamCorrupted` at the precise sequence number where the chain breaks.

## Use cases

### 💰 Financial transaction integrity (banks, fintech, payment processors)

The regulator's question is rarely "show me the transactions" — it's "prove the transactions weren't altered between the time they happened and the time you reported them." Storing every transaction as an event in a hash-chained stream answers this constructively: the chain itself is the proof. A tampered row breaks the chain at a specific sequence number, with a timestamp showing when the break was detected. Forensic accounting becomes a hash recomputation instead of a deposition.

### 🏥 Healthcare records (HIPAA Security Rule)

HIPAA's Security Rule §164.312(c) — *Integrity controls* — requires covered entities to "implement electronic mechanisms to corroborate that electronic protected health information has not been altered or destroyed in an unauthorized manner." Hash-chained event streams are exactly such a mechanism: integrity verification is automatic, continuous, and produces audit-trail evidence by default. Combined with `[EncryptData]` on sensitive fields, the data layer addresses both §164.312(a) (access control) and §164.312(c) (integrity) in the same construction.

### 📜 Forensic admissibility / legal evidence

When an incident produces a legal proceeding — internal investigation, regulator probe, civil suit — the question is whether the event stream is *admissible* as evidence. Append-only storage alone is not enough; the prosecution will ask "could anyone have modified a row after the fact?". Hash chaining lets you answer: *yes, but the chain would have broken at sequence N, and our automated verifier would have alerted at time T*. The chain is the chain-of-custody.

### 🕵️ Insider-threat detection

A DBA with `UPDATE` access to the event store is, by traditional CRUD threat models, an unstoppable insider — they can rewrite history and cover their tracks in the audit log. Hash-chained streams flip this: the verifier runs continuously and independently; covering tracks would require rewriting every subsequent entry's hash *and* defeating the verifier. The window between tampering and detection is bounded by the verifier's poll interval (default 60 seconds), not by when the next external audit happens to look.

### 📊 Audit compliance (SOC 2 Type 2, ISO 27001, SOX)

SOC 2 Type 2 auditors evaluate *operating effectiveness over time* — not just that integrity controls exist, but that they ran throughout the audit period. The `EventStreamHashing` worker emits OpenTelemetry counters and structured log events on every verification pass; pointing the auditor at the dashboard answers the operating-effectiveness question with metrics instead of interview transcripts. Same machinery, same logs, satisfies ISO 27001 Annex A.12 (integrity controls) and SOX Section 404 (internal controls over financial reporting) where event-sourced bookkeeping is in scope.

## What it catches

| Threat | Caught by |
|---|---|
| Edit a single event's payload | Hash recomputation no longer matches the stored hash. |
| Rewrite a stored hash to match a tampered payload | Previous-hash pointer of the next entry no longer matches the rewritten hash. |
| Remove an entry from the middle | Next entry's previous-hash pointer doesn't match the entry that's now its predecessor. |
| Insert a forged entry | Same — pointer chain breaks at the insertion. |
| Restore an older backup that's missing recent entries | If the tail is short, the snapshot/aggregation pass notices the gap; if the chain is internally consistent but historically wrong, this is a backup-policy problem, not a tamper problem — by design. |

## What it does NOT catch

- **Tampering by a process that holds the framework's signing keys.** The hash is a *checksum*, not a signature. Stratara assumes the framework itself and its KMS access have not been compromised. If you need cryptographic non-repudiation against framework-level compromise, sign each entry with an external HSM and verify out-of-band.
- **Backups with tampered hash columns.** If the attacker controls both the data and the backup pipeline, restoring from a tampered backup looks consistent. Defense: backup integrity sits outside Stratara's scope.

## Why SHA-256 specifically

Fast, FIPS-approved, ubiquitous, no patent or export concerns, hardware-accelerated on every modern CPU (`SHA-NI` extension on x86, dedicated instructions on ARMv8.2-A). At ~500 MB/s per core for the algorithm itself, hash computation never becomes the bottleneck — the database write dominates. No measurable production-cost reason to pick anything else.

The chain itself is the cryptographic primitive, not the hash function. Switching to BLAKE3 or SHA-3 would change throughput, not security properties.

## Why it runs as a worker, not on the read path

Two reasons:

1. **Read latency.** Verifying the chain on every read would put a `SELECT ... WHERE Sequence < @current ORDER BY Sequence` on the hot path. Stratara keeps reads cheap by deferring verification.
2. **Failure surface.** A read-path verifier would couple every query handler to the integrity check. Putting the worker on the side means a chain-break alerts an operator without breaking the user-facing request currently in flight.

## See it in action

The hero sample at [`Stratara.Sample.TamperProof`](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.TamperProof) reduces the whole idea to ~100 lines of in-memory code. Run it once with a clean chain (verifier passes), once with a tampered entry (verifier raises `EventStreamCorruptedException` at the exact sequence number).

## Operational notes

- The worker is a `BackgroundService` registered by `AddEventStreamHashWorkerServices()`. It can be scaled horizontally — each instance leases a stream, processes it, releases the lease.
- Verification cadence is configurable via `EventStreamHashingOptions.VerifyEverySeconds` (default 60s). Faster cadence narrows the detection window; slower cadence reduces DB load.
- A detected break **does not** auto-quarantine the stream. It logs at `Error` with the corrupted sequence, raises an OpenTelemetry counter, and lets your alerting decide the response (page on-call, freeze the projection rebuild, write-disable the affected aggregate, …).
- Stratara never *repairs* a broken chain automatically. The fix is operational — confirm what happened, restore from a known-good snapshot or backup, and document the incident.
