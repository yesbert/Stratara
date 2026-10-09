# Restore the read models when a replay fails

> **Status:** approved

## Why

A replay empties every read model first and rebuilds them afterwards. Since 4.0.2 a passing failure is
retried, but a failure that persists, or a host that dies mid-rebuild, still leaves the read store empty or
half-built until a *later* replay gets all the way through. The spec says so in as many words (*A replay
fails partway*: "the read models are left in whatever partial state the replay reached") and hands the
fallback to "the backup taken before it, which is the operator's".

Consumers have paid for that twice. NextPA finding F-012 records the original outage (2026-05-29, a
projection query timing out mid-rebuild); F-021 records the second, on 2026-10-09, when a fact recorded
after a stream's end made the replay fail and left the read store empty. And F-015 records the cost that
never shows up as an incident: a full rebuild has not been run on the consumer's stack since the stream-order
fix shipped, "because starting one empties the read store first" — so a fix meant to make replays safe has
never been exercised where it matters. A replay nobody dares to start is not a recovery tool.

## What Changes

The consumer-visible effect: a host that opts in gets a replay that either completes or leaves the read
models as they were before it began. Readers still see the rebuild in progress while it runs, as today;
what changes is how it ends.

- **New registration** `AddReadModelRestore<TReadContext>(configure)` (PostgreSQL, in
  `Stratara.EventSourcing.EntityFrameworkCore`). Before a replay empties anything, the tables the read
  context maps are copied aside in one consistent snapshot — the views, the projection checkpoints and the
  record of forgotten tenants together, so the restored state is one that existed.
- **A failed replay restores them.** After the last retry fails, the copy is written back in one
  transaction and dropped; readers see either the rebuild's last state or the restored one, never a mix.
  `ReplayOutcome.ReadModelsRestored` says it happened.
- **A replay whose host died is restored at the next start.** A copy that no running replay owns is
  restored when the next host with the registration starts, under a database lock so only one host does it.
  A replay that is requested while such a copy exists keeps that copy — it is the last complete state —
  rather than preserving the half-built one.
- **A copy that cannot be made fails the replay before anything is emptied.** So does a table outside the
  set that a foreign key ties into it, which the restore could not write back.
- **A successful replay drops the copy.**
- New abstraction `IReadModelPreservation` (in `Stratara.Projections`) for a store other than PostgreSQL;
  the replay worker uses it when one is registered and behaves exactly as today when none is.

Not changed: a host without the registration replays as today; what a replay reads, the order it applies,
the retry, the lease, the progress reporting. Readers are not shielded from the rebuild while it runs —
building beside the live views and swapping was considered and rejected for this change (see design).

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `projections`: *A replay truncates every read model before rebuilding* gains the restore a host can opt
  into, and *A replay fails partway* states both endings; *A replay retries a failing batch before it fails*
  no longer names the operator's backup as the only fallback.

## Impact

- `src/Stratara.Projections/Abstractions/IReadModelPreservation.cs` (new) — `PreserveAsync`,
  `RestoreAsync`, `DiscardAsync`, `RestoreAbandonedAsync`.
- `src/Stratara.Projections/Services/ProjectionReplayWorker.cs` — preserve first, restore on failure,
  discard on success, leave the copy on interruption; the abandoned-copy check at start.
- `src/Stratara.EventSourcing.EntityFrameworkCore/ReadStore/Replay/` (new) — the PostgreSQL implementation,
  `ReadModelRestoreOptions`, `AddReadModelRestore<TReadContext>()`.
- `src/Stratara.Abstractions/Abstractions/Projections/IProjectionReplayState.cs` — `ReplayOutcome.ReadModelsRestored`
  (builds on `keep-the-outcome-of-the-last-replay`).
- `src/Stratara.Diagnostics/LogEvents.cs` and the projection logger extensions — preservation, restore,
  restore failure, abandoned copy.
- `docs/guides/write-a-projection.md` (*Replay is destructive, and it is all-or-nothing*),
  `docs/guides/operate-the-orleans-execution-model.md` (*A rebuild or a replay that does not finish*),
  `docs/reference/di-extensions-cheatsheet.md`, `CHANGELOG.md`.
- Depends on `keep-the-outcome-of-the-last-replay` (request id, outcome). No new package or dependency;
  the PostgreSQL package already references `Stratara.Projections`. Additive: a patch release.
