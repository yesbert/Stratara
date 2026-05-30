# Tamper-Evident Streams

## The problem

Append-only event stores have an obvious blind spot. The events themselves are immutable *by convention* — domain code never overwrites a row. But the rows are still rows in a database. A malicious admin, a buggy migration, an ad-hoc `UPDATE` through the wrong session, a restored backup that swaps in different bytes — and the immutability promise is gone.

A normal aggregate replay won't notice. Projections happily rebuild from the wrong events. The first sign of trouble is usually a customer noticing their balance is off.

## How Stratara solves it

Every entry in the event stream carries two extra columns: a **hash** of the entry's contents and a **previous-hash** pointer to the prior entry's hash. Simplified, the hash is `SHA-256(previousHash || event-payload)` — in practice the input also binds the entry's global sequence number, version, timestamp and event type, so an entry's *position and identity* are part of the hash, not just its payload.

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

A background worker (`EventStreamHashing`, one of the composites in `Stratara.EventSourcing.WorkerDefaults`) hashes each newly-committed event into the chain a beat behind the write. That's what makes tampering *evident*: because every entry's hash is pinned to the one before it, recomputing the chain and comparing surfaces any divergence — payload mutated, hash column rewritten, an entry removed in the middle — at the precise sequence number where the break is. Running that comparison is a deliberate verification pass (an audit job, an external-anchor check; the hero sample's `ChainVerifier` is exactly this check), not something the framework does to itself automatically. The chain preserves the evidence; you decide when to check it.

## Use cases

### 💰 Financial transaction integrity (banks, fintech, payment processors)

The regulator's question is rarely "show me the transactions" — it's "prove the transactions weren't altered between the time they happened and the time you reported them." Storing every transaction as an event in a hash-chained stream answers this constructively: the chain itself is the proof. A tampered row breaks the chain at a specific sequence number, with a timestamp showing when the break was detected. Forensic accounting becomes a hash recomputation instead of a deposition.

### 🏥 Healthcare records (HIPAA Security Rule)

HIPAA's Security Rule §164.312(c) — *Integrity controls* — requires covered entities to "implement electronic mechanisms to corroborate that electronic protected health information has not been altered or destroyed in an unauthorized manner." Hash-chained event streams are exactly such a mechanism: integrity verification is automatic, continuous, and produces audit-trail evidence by default. Combined with `[EncryptData]` on sensitive fields, the data layer addresses both §164.312(a) (access control) and §164.312(c) (integrity) in the same construction.

### 📜 Forensic admissibility / legal evidence

When an incident produces a legal proceeding — internal investigation, regulator probe, civil suit — the question is whether the event stream is *admissible* as evidence. Append-only storage alone is not enough; the prosecution will ask "could anyone have modified a row after the fact?". Hash chaining lets you answer: *yes, but the chain would have broken at sequence N, and our automated verifier would have alerted at time T*. The chain is the chain-of-custody.

### 🕵️ Insider-threat detection

A DBA with `UPDATE` access to the event store is, by traditional CRUD threat models, an unstoppable insider — they can rewrite history and cover their tracks in the audit log. Hash-chained streams raise the bar: editing one row leaves a break that any verification recomputes and finds. Covering tracks means rewriting every subsequent entry's hash — a full re-chain — and even that is defeated once anchors are committed to a source of truth outside the database (see [Anchoring beyond your own infrastructure](#anchoring-beyond-your-own-infrastructure)). The externally committed hash is the one thing the insider can't reach.

### 📊 Audit compliance (SOC 2 Type 2, ISO 27001, SOX)

SOC 2 Type 2 auditors evaluate *operating effectiveness over time* — not just that integrity controls exist, but that they ran throughout the audit period. The `EventStreamHashing` worker emits structured log events as it hashes; that running record, plus a scheduled verification job over the chain, is the evidence the control actually operated — instead of an interview transcript. The same machinery speaks to ISO 27001 Annex A.12 (integrity controls) and SOX Section 404 (internal controls over financial reporting) where event-sourced bookkeeping is in scope.

## What it catches

| Threat | Caught by |
|---|---|
| Edit a single event's payload | Hash recomputation no longer matches the stored hash. |
| Rewrite a stored hash to match a tampered payload | Previous-hash pointer of the next entry no longer matches the rewritten hash. |
| Remove an entry from the middle | Next entry's previous-hash pointer doesn't match the entry that's now its predecessor. |
| Insert a forged entry | Same — pointer chain breaks at the insertion. |
| Restore an older backup that's missing recent entries | A verification pass sees the chain end early; a chain that is internally consistent but historically stale is a backup-policy problem, not a tamper problem — by design. |

## The one attack the per-stream chain can't stop on its own

The table above assumes the attacker edits *some* rows and leaves the rest. But an insider with
full write access to your database can do more: after editing a payload, they **recompute every
hash in the stream**. The chain is internally consistent again, and the verifier sees nothing
wrong. The per-stream hash is a *checksum, not a signature* — when you own both ends of the
chain, so does anyone who compromises that database.

## Anchoring beyond your own infrastructure

This is what Stratara's **event-chain anchor** is for. Alongside the per-stream chains, a second
table (`event_chain_anchor`) records periodic global anchors: every N events the
`EventStreamHashing` worker writes an anchor row capturing the chain head at that sequence. Each
anchor row carries an optional **`BlockchainTxHash`** column — the seam for committing that anchor
to a source of truth **outside your own infrastructure**:

- a public blockchain (the anchor hash as a transaction payload),
- an RFC 3161 timestamp authority or an [OpenTimestamps](https://opentimestamps.org/) calendar,
- a notary, a transparency log, or simply a second party you don't control.

Once an anchor is committed externally, the full re-chain attack fails. The insider can rewrite
every hash in your database — but cannot change the hash already published beyond their reach.
Verification escalates from *"is the local chain internally consistent?"* to *"does the local
chain still match what we committed externally?"*.

**What ships today vs. what you wire.** The anchor table, the periodic anchor worker, and the
`BlockchainTxHash` seam are in the box. The actual submission to an external chain — and
re-verification against it — is the integration point you own: Stratara stays
application-agnostic about *which* external source of truth you trust, the same way it doesn't
pick your message broker or your KMS. The [`TamperProof` sample](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.TamperProof)
demonstrates the whole escalation in-memory: a full re-chain that passes local verification but
fails against an external anchor.

## What it does NOT catch

- **An attacker who controls your database *and* the external anchor pipeline.** Anchoring only
  helps if the source of truth is genuinely outside the attacker's reach. If they can forge the
  external commitment too — or you never wired external anchoring in the first place — a
  fully re-chained, re-anchored stream looks consistent. Choose an anchor target you don't control.
- **Backups with tampered hash columns, restored without anchor re-verification.** If the attacker
  controls both the data and the backup pipeline, a restored tampered backup looks internally
  consistent. External anchoring narrows this; backup integrity otherwise sits outside Stratara's scope.

## Why SHA-256 specifically

Fast, FIPS-approved, ubiquitous, no patent or export concerns, hardware-accelerated on every modern CPU (`SHA-NI` extension on x86, dedicated instructions on ARMv8.2-A). At ~500 MB/s per core for the algorithm itself, hash computation never becomes the bottleneck — the database write dominates. No measurable production-cost reason to pick anything else.

The chain itself is the cryptographic primitive, not the hash function. Switching to BLAKE3 or SHA-3 would change throughput, not security properties.

## Why hashing runs on a worker

Hashing happens on a background worker rather than inline on every append, so writes stay cheap — the chain is filled in a beat behind the commit, not inside the request. Verification is then a separate, deliberate pass: a scheduled audit job, or the external-anchor check above. You don't want it on the read path either — that would put a `SELECT ... ORDER BY Sequence` on every query and couple each read to the integrity check. Run it on its own schedule, and a detected break alerts an operator instead of failing whatever request is in flight.

Worth stating plainly: nothing in the framework wakes up and scans for tampering on its own today. The hashes and anchors exist so that *when* you verify — on a schedule, during an audit, after an incident — the evidence is intact and a break shows up at the exact sequence.

## See it in action

The hero sample at [`Stratara.Sample.TamperProof`](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.TamperProof) reduces the whole idea to in-memory code, in three acts:

1. **Clean chain** — the verifier passes.
2. **Naive tamper** — one payload edited, hash left stale; the verifier raises `EventStreamCorruptedException` at the exact sequence number.
3. **Full re-chain + external anchor** — the attacker recomputes every hash so local verification *passes again*, but the run still catches it by checking the chain head against an anchor published to a (simulated) external source of truth.

## Operational notes

- The worker is a `BackgroundService` registered by `AddEventStreamHashWorkerServices()`.
- The same worker drives anchoring: after each hashing batch it writes an `event_chain_anchor` row once enough new events have accrued (the anchor range), capturing the chain head at that sequence. Committing the anchor to an external source of truth and storing the reference on `BlockchainTxHash` is the integration point you wire — Stratara records the anchor; it does not pick the external chain for you.
- The worker hashes newly-committed events on a fixed interval (a few seconds behind the commit, to let concurrent writers flush). Verification — recomputing the chain to look for a break — is something you schedule yourself, as an audit job or as part of the external-anchor check; it is not driven by the hashing worker.
- When a verification pass finds a break, it surfaces the corrupted sequence number. Stratara does **not** auto-quarantine the stream — your alerting decides the response (page on-call, freeze the projection rebuild, write-disable the affected aggregate, …).
- Stratara never *repairs* a broken chain automatically. The fix is operational — confirm what happened, restore from a known-good snapshot or backup, and document the incident.
