# let-a-paused-reader-always-come-back

> **Status:** approved (owner, 2026-09-18 — recorded at the owner's request)

## Why

A projection's rebuild, the full replay's checkpoint reset and the execution-model reset pause the
store readers they change the checkpoints of. Today a store reader counts its pausers and knows none of
them, and the count lives only in the activation. Three ways it goes wrong were left open on purpose in
`let-the-read-side-be-reset-where-it-runs` (D2, "Why not a pauser lease"), on the ground that a reader
resumed too early costs only a re-read. The pre-4.2.0 review found that ground false:

- **A caller that dies between pause and resume leaves the readers paused until their silo restarts.**
  Nothing counts it: a paused reader is not a stall, so `orleans.reader.stalled` stays quiet, and the
  projection's read model silently stops following the store.
- **A resume that is retried can release another rebuild's pause.** A resume whose answer is lost was
  applied all the same; the retry takes a second pauser off, and that one belonged to a rebuild still
  emptying its read model.
- **A reader that is resumed too early loses facts from the rebuilt read model.** The rebuild returns
  the checkpoints to the beginning and then empties the read model. A reader that reads in between —
  after an early resume, or after its activation moved to another silo and forgot the count — applies
  facts, advances its checkpoint past them, and then watches the truncate delete them. Re-reading does
  not repair it, because the checkpoint already says the facts were applied. The read model is missing
  them until somebody rebuilds again, and nothing says so.

The owner asked that 4.2.0 ship with no known gap, so this closes all three rather than recording them.

## What Changes

- **A pause belongs to its pauser and lapses without it.** Every pause carries the pauser's identity
  and a lease the pauser renews while it works. Resuming releases only the resuming pauser's pause,
  however often it is retried. A pause whose pauser stops renewing lapses, the reader resumes, and the
  lapse is logged (new log event, Warning) naming the reader.
- **A rebuild and a replay return the checkpoints to the beginning a second time, after the read
  model is emptied.** Whatever a reader applied before the truncate is then read again after it; the
  guarded advance refuses a reader that still holds its pre-truncate position, so it re-reads its
  checkpoint. Projections already apply idempotently under a full replay; a rebuild now relies on the
  same property for the window between the truncate and the second reset.
- **A reader moved to another silo mid-rebuild is covered by the second reset** rather than by
  persisting the pause: the pause stays in memory, and what an activation that forgot it reads is
  undone before the rebuild resumes.
- **Rolling upgrade.** The grain keeps its current pause and resume methods beside the new ones, so a
  silo on 4.1.x can still pause and resume a reader hosted on a silo already on 4.2.0; such a pause
  carries a lease of its own and lapses like any other.

Consumer-visible: a dead rebuilder's readers come back on their own within the lease; a rebuilt read
model holds every fact of the store however the pause was released. Nothing in the public API changes.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`: *Projections and sagas read the store in commit order and never miss a
  committed fact* — a pause is held by its pauser and lapses without it; resuming releases only the
  resuming pauser's pause; a rebuild's read model holds every fact however early its readers resume.

## Impact

- Affected specs: `orleans-execution`
- Affected code: `src/Stratara.Orleans/Projections/StoreReaderGrain.cs`,
  `src/Stratara.Orleans/Projections/StoreReaderPause.cs`,
  `src/Stratara.Orleans/Projections/ProjectionGrain.cs` (`IProjectionGrain`, the `INudgeTarget` pause
  port), `src/Stratara.Orleans/Sagas/SagaGrain.cs` (`ISagaGrain`, the pause port),
  `src/Stratara.Orleans/Projections/ProjectionRebuilder.cs`,
  `src/Stratara.Orleans/Projections/ReplayCheckpointReset.cs`,
  `src/Stratara.Testing.Orleans/InMemoryExecutionModelReset.cs`,
  `src/Stratara.Orleans/Diagnostics/OrleansLog.cs`, `src/Stratara.Diagnostics/LogEvents.cs`
- Affected docs: `docs/guides/operate-the-orleans-execution-model.md` (rebuild and replay: what a
  dead rebuilder leaves behind), `docs/reference/log-events-schema.md`, `CHANGELOG.md`
- Superseded decision: `openspec/changes/archive/2026-09-18-let-the-read-side-be-reset-where-it-runs/design.md`
  D2, paragraph "Why not a pauser lease" — its premise that an early resume is repaired by re-reading
  does not hold, see design.md D1.
- Public API: none. The grain interfaces and the pause port are internal.
- Schema: none.
- Versioning: part of 4.2.0 (minor already planned).
