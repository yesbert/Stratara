## Context

A store reader (`StoreReaderGrain`, the base of the projection and saga grains) holds its pause as
`int _pausers` (`StoreReaderGrain.cs:40`). `PauseAsync` adds one and waits for a running batch;
`ResumeAsync` takes one off and restarts the reader at zero (`:66-91`). The count is in memory; the
grain's poll timer keeps the activation alive (`:109-117`), so it is lost only when the activation is,
which in practice means its silo died.

The callers go through `StoreReaderPause` (`StoreReaderPause.cs`):
`ProjectionRebuilder.RebuildAsync` (pause, reset every partition's checkpoint, truncate, resume —
`ProjectionRebuilder.cs:38-65`), `ReplayCheckpointReset.TruncateAllAsync` (the same for every
projection, `:38-54`), and the `INudgeTarget` pause port on the projection and saga grains
(`ProjectionGrain.cs:301-305`, `SagaGrain.cs:125-129`) that the test host's execution-model reset
uses (`InMemoryExecutionModelReset.cs:47,62`). Every interface involved is internal.

## Goals / Non-Goals

**Goals:** a pause that ends when its pauser does; a resume that cannot release someone else's pause;
a rebuilt read model that is complete however early a reader resumed.

**Non-Goals:** persisting the pause across a silo's death (D3 makes it unnecessary); changing how a
saga's checkpoint is reset (the saga reader is reset only with the deployment stopped, unchanged —
`isolate-a-failing-saga` changes the saga consumers, not this).

## Decisions

### D1 — The premise of "Why not a pauser lease" does not hold

`let-the-read-side-be-reset-where-it-runs` D2 (2026-09-18) chose the counting pauser over a lease
because "a reader that resumes once too early … is a rebuild reading live events a moment too soon,
which re-reading repairs". It does not: the rebuild resets the checkpoints *before* it truncates
(`ProjectionRebuilder.cs:54-57`). A reader resumed in between reads from the beginning, applies facts,
and advances its checkpoint past them; the truncate then deletes those rows and the checkpoint still
says they were applied. Evidence: the implementation, read on 2026-09-18 during the pre-4.2.0 review.
That decision's other half — that a reader stuck paused is worse than one resumed early — stands and
is why D2 below lets a pause lapse rather than hold forever.

### D2 — A pause has an owner and a lease

**Decision.** The grain keeps its pausers as a map from pauser id to expiry. New aliased grain methods
`PauseAsync(Guid pauser, TimeSpan lease)`, `RenewPauseAsync(Guid pauser, TimeSpan lease)` and
`ResumeAsync(Guid pauser)`. The expiry is taken from the grain's own `TimeProvider` when the call
arrives, so no clock is compared across silos. Resuming an id the grain does not hold does nothing,
which is what makes a retried resume safe. The grain's poll tick drops every expired pauser; when the
last one goes by expiry, the reader logs the lapse (new event `117_123` `StoreReaderPauseLapsed`,
Warning, with the reader's key) and restarts as a resume does.

The pauser side is a hold: `StoreReaderPause.PauseAllAsync` returns an `IAsyncDisposable` that renews
every paused reader at a third of the lease and resumes them all on dispose. The renewal loop is an
async method whose task the hold keeps and awaits on dispose — no `Task.Run`. Lease 60 s, renewal 20 s;
internal constants, with the test host shortening them the way it shortens the other periods.

**Why not an operator-set option.** The lease only bounds how long a dead pauser's readers wait. A
live rebuild renews for as long as it runs, however long its truncate takes, so there is no workload
that needs a different value.

**Why not the pauser's own heartbeat grain.** A grain per rebuild that the readers ask about would
need a call per reader per tick and a second failure path; a lease the reader expires on its own tick
needs neither.

### D3 — Return the checkpoints to the beginning again after the truncate

**Decision.** Rebuild and replay reset become pause → reset → truncate → reset → resume. The first
reset stays because *A rebuild fails part-way* promises that a failed truncate is followed by a
re-read. The second makes anything a reader applied before the truncate — after a lapse, or in an
activation that moved to another silo and forgot the pause — read again after it. A reader that still
holds its pre-truncate position is refused by the guarded advance (`AdvanceAsync`, since
`keep-a-store-reader-batch-tenant-correct-and-its-checkpoint-guarded`) and reads its checkpoint again.

**The cost.** A fact applied between the truncate and the second reset is applied twice. That needs
the projection to apply idempotently — the property a full replay already requires of every
store-reading projection (the spec's own wording, *Projections and sagas read the store…*) — and it
happens only where a reader was resumed early, which D2 makes rare.

**Why not persist the pause.** A pause table is a schema change for every consumer and a write per
pause and renewal, to protect a window the second reset already makes harmless.

### D4 — Rolling upgrade keeps the old methods

The parameterless `PauseAsync` and `ResumeAsync` stay under their aliases. A legacy pause registers an
anonymous pauser with a 10-minute lease (its caller cannot renew); a legacy resume releases the oldest
anonymous pauser. A 4.1.x rebuilder paused for longer than ten minutes during a rolling upgrade would
lose its pause early; D3 does not protect it because the old rebuilder does not reset twice. That is
the upgrade window only, and it is written into the upgrade note.

## Risks / Trade-offs

- [A live rebuilder's renewal is delayed past the lease — a long GC pause, a saturated silo] → the
  pause lapses mid-rebuild, the lapse is logged, and D3 keeps the read model complete.
- [A projection that does not apply idempotently sees a fact twice after an early resume] → the same
  requirement a full replay already makes; the operate guide says so where it describes the rebuild.
- [A 4.1.x rebuilder during a rolling upgrade] → D4; stated in the upgrade note.

## Migration Plan

No schema. Upgrade silos in any order; the old grain methods remain until the next major version.
Rollback to 4.1.x is safe: a 4.1.x silo ignores nothing it relies on.

## Decisions taken during implementation (2026-09-18)

Evidence for each: the tests named, run on the PostgreSQL store.

- **D5 — A resume for a pauser the grain does not hold makes it read its checkpoint again.** It
  releases nothing (D2), but clears the reader's cached position. Without it D3 has a hole: a reader
  that lapsed or moved and advanced before the truncate keeps its old position after the second reset,
  and where no new fact commits the guarded advance never refuses it — the read model stays empty until
  the next commit. Evidence: `PausedReaderLeaseTests` (the early-resume case fails with "model holds 0
  of 12 rows" without it).
- **D6 — A renewal for a pauser the grain does not hold pauses it again**, without waiting for a
  running batch, so a reader that lapsed or moved comes back under the live rebuild's pause. A renewal
  delayed past the resume can cause one spurious pause, which lapses after one lease and is logged. The
  hold stops its renewal loop and waits for it before it resumes, so only a message delayed in transit
  can do this.
- **D7 — A pause starts its lease once the running batch has ended**, so a long batch does not use up
  the lease before the pauser can renew it.
- **D8 — The pause port returns the hold.** `INudgeTarget.PauseAsync` returns the hold, which resumes;
  the port's separate resume is gone. Internal.
- **D9 — Lease and renewal period are an internal singleton** (`StoreReaderLease`, 60 s / 20 s) rather
  than constants, so the test host can shorten them with its other periods. Nothing public.
- **D10 — The second reset always runs**, with `CancellationToken.None`, also after a failed truncate;
  where both fail the caller receives both as an `AggregateException`.
- **D11 — The rebuild quiesces the readers between the truncate and the second reset.** A reader
  that lapsed or moved may read from the first reset, apply part of a batch, and still be applying it
  when the model is emptied. The second reset writes the same beginning the reader started from, so the
  guarded advance (stored = from) accepts its write after the reset and the entries it applied before
  the truncate are missing. The hold therefore pauses every reader again under its own pauser
  (`QuiesceAsync`) — idempotent for a reader that still holds it, and waiting for a batch in flight —
  before the second reset; quiescing and the second reset run however the truncate ended. Evidence:
  `PausedReaderLeaseTests.A_batch_in_flight_across_the_truncation_leaves_the_model_complete` (fails
  with "2 of 3 rows" without the quiescing), `ProjectionRebuilderTests.The_readers_are_quiesced_between_the_truncation_and_the_second_reset`.
- **D12 — The rebuild's protection rests on a guarded advance, and says so.** The default
  `IProjectionCheckpointStore.AdvanceAsync` replaces the position unchecked; its XML documentation and
  the operate guide say that a store used with a rebuild or a replay must override it with the guard.
  The framework's store does. Documentation only.
- **D13 — An invalidation during a checkpoint read wins over the read.** The reader counts its
  invalidations and marks a position it read as known only where none happened during the read, so a
  resume that clears the position (D5) while a catch-up is reading the checkpoint is not undone on a
  quiet partition. Evidence: `StoreReaderLoopTests.An_invalidation_during_a_checkpoint_read_is_not_overwritten_by_the_read`.
- **D14 — A released pauser is remembered for one lease.** A pause or renewal of it delivered after its
  resume pauses nothing and logs no lapse. A lapsed pauser is not remembered and an activation that
  moved has no memory, so D6 still holds. Evidence: `StoreReaderPauseTests.A_released_pauser_is_remembered_for_one_lease_so_a_late_pause_or_renewal_holds_nothing`,
  `PausedReaderLeaseTests.A_resume_or_a_renewal_for_a_pauser_the_reader_does_not_hold` (grain level:
  an unknown resume releases nothing, a late renewal pauses nothing, an unseen renewal pauses until
  its resume).
- **D15 — The hold renews from the moment the pauses are sent**, not once all have answered, so a
  reader that answered at once does not lapse while another's pause waits for a long batch; a failed
  pause stops the renewal before it resumes. Evidence: `StoreReaderPauseTests.A_hold_renews_the_readers_that_paused_while_another_pause_still_waits`,
  `A_pause_that_fails_stops_renewing_before_it_resumes`.
- **D16 — D7, amended: a pause holds again after its wait unconditionally** — also where its lease
  lapsed during a long wait — unless its pauser was released meanwhile (D14).
- **D4, amended — a rebuild during a rolling upgrade.** A rebuild started from a 4.2.0 silo cannot
  pause a reader still hosted on a 4.1.x silo, which has no held pause; it fails naming that reader,
  which is safe. The operate guide and the CHANGELOG say to rebuild once every silo runs 4.2.0.
