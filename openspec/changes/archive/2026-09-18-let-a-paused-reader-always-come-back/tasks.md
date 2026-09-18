# Tasks

## 1. The reader

- [x] 1.1 `StoreReaderGrain`: pausers as id → expiry from the grain's `TimeProvider`; `PauseAsync(pauser, lease)`,
      `RenewPauseAsync(pauser, lease)`, `ResumeAsync(pauser)` as new aliased methods on `IProjectionGrain` and
      `ISagaGrain`; resuming an unknown id does nothing.
- [x] 1.2 The poll tick drops expired pausers; the last one to lapse restarts the reader and logs `117_123`
      `StoreReaderPauseLapsed` (Warning) — `LogEvents.cs`, `OrleansLog.cs`.
- [x] 1.3 Legacy parameterless pause and resume map to an anonymous pauser with a 10-minute lease (D4).
- [x] 1.4 Unit tests (`tests/Stratara.Orleans.Tests`): a resume delivered twice releases one pause; an unrenewed
      pause lapses at its lease and logs; a renewed one does not.

## 2. The pauser

- [x] 2.1 `StoreReaderPause.PauseAllAsync` returns a hold that renews at a third of the lease and resumes on
      dispose; the failure semantics of today (a failed pause resumes what paused and names the rest) are kept.
- [x] 2.2 `ProjectionRebuilder` and `ReplayCheckpointReset`: pause → reset → truncate → reset → resume.
- [x] 2.3 The `INudgeTarget` pause port carries the hold; `InMemoryExecutionModelReset` uses it.
- [x] 2.4 The test host shortens lease and renewal with its other periods.
- [x] 2.5 Unit tests in `ProjectionRebuilderTests`: the second reset follows the truncate; a failed truncate still
      leaves the checkpoints at the beginning.

## 3. End to end (PostgreSQL)

- [x] 3.1 `tests/Stratara.Orleans.IntegrationTests/Projections`: a rebuilder that pauses and never resumes — the
      readers resume after the lease, the lapse is logged, a later event reaches the read model.
- [x] 3.2 `OverlappingRebuildTests`: the first rebuild's resume delivered twice — the readers stay paused until the
      second rebuild ends; the read model holds every fact.
- [x] 3.3 A reader resumed between the reset and the truncate — the read model holds every fact after the rebuild.

## 4. Documentation

- [x] 4.1 `docs/guides/operate-the-orleans-execution-model.md`: what a dead rebuilder leaves (readers back within
      the lease, a logged lapse) and that a rebuild may apply a fact twice after an early resume.
- [x] 4.2 `docs/reference/log-events-schema.md`: `117_123`.
- [x] 4.3 `CHANGELOG.md` `[Unreleased]` → *Fixed*: the three failures, and the upgrade note of D4.
