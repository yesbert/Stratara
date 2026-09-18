## Context

`SagaGrain` (`src/Stratara.Orleans/Sagas/SagaGrain.cs`) is one `StoreReaderGrain` per partition, key
`sagas/<partition>`, checkpoint consumer `sagas`. Per entry it calls `ISagaManager.HandleAsync`, which
runs every registered saga in parallel (`SagaManager.cs:21-22`), then hands the facts a stateful
process handles to that process's grains (`SagaGrain.cs:96-104`). The entry runs inside
`StoreReaderLoop.ApplyEachAsync`: a missing prerequisite is retried under the preceding-fact policy, any
other failure stalls the partition and the entry is retried on every poll and wake-up, unbounded.

Projections already work the way this change makes sagas work: one reader per projection and partition,
keyed by the projection's name (`ProjectionGrain`, `ProjectionNudgeTarget`).

## Goals / Non-Goals

**Goals:** a failing saga repeats nothing but itself and stops nothing but itself; a saga added later,
or the first start after the upgrade, runs no side effect for history the host's sagas already read.

**Non-Goals:** a bound on a failing saga's retries and a kept state for its entry (would need storage —
not chosen by the owner, 2026-09-18); sharing a read store's sagas between deployments; the pause
(`let-a-paused-reader-always-come-back`).

## Decisions

### D1 — One reader per saga and partition

**Decision.** The saga grain's key becomes `sagas:<SagaName>/<partition>`, `SagaName` being the name
`ISagaHandler.GetSagaName` gives (the type name) — the consumer name its checkpoint is kept under.
A stateless saga's reader maps the entry and calls `ISagaHandler.HandleAsync(saga, relevant events)`
for its one saga, skipping entries none of whose events it declares. A process's reader forwards the
facts it handles to the process grains, as the shared reader does today. `SagaNudgeTarget` lists every
saga's consumer name and nudges, ensures, pauses and resumes each.

**Why not remember which sagas of a stalled entry succeeded.** It lives in memory; a failover forgets
it and repeats the siblings again, and the failing saga still stops the partition for every other saga.

**Why the saga's type name.** It is what the handler, the logs and the metrics already call a saga.
Renaming a saga class makes it a new consumer, which D2 starts where the others read — not at the
beginning.

**Cost.** Grains per saga role grow from one per partition to one per saga and partition, as for
projections. Each reads the store on its own; with a handful of sagas that is a handful of reads per
wake-up where there was one.

### D2 — Where a saga without a checkpoint starts

**Decision.** `StoreReaderGrain` gains an overridable starting position for a consumer with no
checkpoint (default 0, which keeps projections as they are). The saga grain supplies: the legacy
`sagas` checkpoint of the partition if one exists; otherwise the highest checkpoint any other saga of
the host holds in the partition; otherwise 0. The position found is written as the saga's own
checkpoint under the host's reader name before the first read, so a restart does not ask again.

**Why not the beginning, as a projection does.** A projection rebuilt from the beginning is correct; a
saga run from the beginning sends every email and issues every command of the store's history again.
Before this change a saga registered later started where the shared reader was; this keeps that.

**Why the highest and not the lowest other checkpoint.** The lowest may belong to a saga stalled for
days; starting there would replay those days into the new saga. The highest is where the host's sagas
have got to — the same point the shared reader stood at.

### D3 — The upgrade

A saga-role silo on 4.2.0 still has a grain class for the legacy key `sagas/<partition>`: when the
keep-alive reminder of a 4.1.x deployment brings it back, it removes its reminder, logs that it retired
(new event, Information) and reads nothing, like a reader beyond a lowered partition count. The legacy
checkpoint row stays as the starting point D2 reads, and the execution-model reset removes it with the
host's other checkpoints. While a 4.1.x saga silo still runs, its shared reader and the new per-saga
readers both apply facts above the legacy checkpoint — at-least-once, which a saga already tolerates;
the upgrade note recommends upgrading the saga silos together.

## Risks / Trade-offs

- [A dashboard filters stalls on the consumer `sagas`] → the CHANGELOG names the new consumer names.
- [A fact reaches a saga twice during a rolling upgrade] → D3; stated in the upgrade note.
- [A saga that is renamed starts at the highest other saga's checkpoint, not its own old one] → the
  same as a renamed projection starting at the beginning: the checkpoint is keyed by name, and the
  documentation says so.

## Migration Plan

No schema. Upgrade the saga-role silos together where possible. Rollback to 4.1.x: the 4.1.x shared
reader resumes from the legacy checkpoint, which the new version never advanced — facts the per-saga
readers applied since the upgrade are applied again by the shared reader. The rollback note says so.

## Decisions taken during implementation (2026-09-18)

- **D4 — A missing checkpoint is told apart from one at the beginning, through the public port.** `GetAsync`
  answers 0 for both; treating 0 as missing would let a saga stalled on its first entry jump to a sibling's
  position on reactivation and skip that fact, and would let a later-activated sibling on a fresh deployment start
  past facts. `IProjectionCheckpointStore` gains `FindAsync` (the position or `null`) and `CreateAsync`
  (insert-only), additive members whose defaults throw `NotSupportedException` naming both; the framework's EF store
  implements them, and a loser of the insert race reads the winner's row and writes nothing. A saga reader on a store
  without them fails its start with that message; projections do not use them. The first draft's internal
  `IFirstCheckpointStore`, with a fallback that treated 0 as missing, was retired after review. Evidence:
  `SagaReaderTests`, `ProjectionCheckpointGuardTests`, `ProjectionCheckpointStoreTests`.
- **D5 — A reader creates every missing saga checkpoint of the host, not only its own.** Otherwise, on a
  fresh deployment, one saga's reader could start and advance before a sibling's activated, and the
  sibling would then start past facts it never saw. Creation never overwrites, so readers starting
  together agree on the start. Cost: one `FindAsync` per saga on each reader activation.
- **D2, amended — the start is the maximum of the legacy checkpoint and the host's other saga
  checkpoints.** "Legacy if present" would start a saga added after the upgrade at the old shared
  checkpoint and replay everything applied since.
- **D3, amended — the legacy grain retires without deactivating at once.** Deactivating on activation
  let a 4.1.x silo's call bounce between activations until the runtime refused it; the grain removes its
  reminder, logs `117_125` `SharedSagaReaderRetired` and answers each call as a no-op until it is
  collected idle. An unregistered saga's reader (D8) deactivates at once instead, which is why the retirement
  names its kind.
- **D6 — Saga names come from the registrations, once.** `SagaNudgeTarget` listed the consumers by resolving every
  `ISaga` and the saga handler in each scope, which built every saga — and its dependencies — on every commit, so a
  saga with a broken dependency broke commit dispatch. A singleton reads the registrations' implementation types (a
  factory registration is built once, to learn its type); the saga's name is its type name, which is what
  `ISagaHandler.GetSagaName` answers.
- **D7 — Two saga types of one name stop the host at start.** They would share one checkpoint. The check runs from
  the registrations in a hosted service ahead of the silo, naming both types — not at the reader's first entry, as
  the first draft did.
- **D8 — A reader whose saga the silo does not register retires.** A removed or renamed saga's reader was brought
  back by its keep-alive and stalled on every entry for ever. It now unregisters its keep-alive, logs `117_126`
  `UnregisteredSagaReaderRetired` and deactivates at once, so a silo that still registers the saga — a rolling
  deployment — can host it.
- **D9 — Seeding a store whose sagas shared a checkpoint follows D2.** Seeding put every `sagas:<Name>` at the head,
  skipping a lagging shared reader's backlog; a saga consumer without its own checkpoint in a partition with the
  shared checkpoint is seeded where its reader would start it.
- **D10 — A stateless saga's reader passes over entries its saga does not handle before deserialising them.** The
  entry's stored type name is run through the upcasters and the trusted-type resolution once per name and compared
  with the event types the saga declares; the reader learns those from its first resolved saga.
- **Added** — the operate guide states the per-saga cost (S×P activations and keep-alive reminders, S×P readers
  ensured at start, S×S×P existence queries on a cold start) and to size the connection pools for it.
