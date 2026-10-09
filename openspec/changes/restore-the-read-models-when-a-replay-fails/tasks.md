## 1. Reproduce first

- [x] 1.1 `tests/Stratara.Orleans.IntegrationTests/Projections/ReplayRestoreTests.cs` (new; the project owns
  the PostgreSQL container and a real read context): `A_replay_that_fails_after_truncating_leaves_the_read_models_partial_without_the_registration`
  documents today's behaviour (green on `main`) and becomes the "without the registration" fact.
- [x] 1.2 Same file, red until the registration exists:
  `A_replay_that_fails_restores_the_views_the_checkpoints_and_the_forgotten_tenants`,
  `A_replay_that_succeeds_leaves_no_copy`,
  `A_copy_left_by_a_dead_replay_is_restored_when_the_next_host_starts`,
  `Two_hosts_starting_over_one_abandoned_copy_restore_it_once`,
  `A_replay_requested_over_an_abandoned_copy_keeps_that_copy`,
  `A_foreign_key_from_outside_the_set_fails_the_replay_before_anything_is_emptied`,
  `A_restore_after_a_column_change_refuses_and_keeps_the_copy`,
  `Identity_sequences_continue_above_the_restored_rows`, and the end-to-end replays through the replay worker on a
  host composed with `AddEventProjectionServices`.

## 2. The abstraction and the worker

- [x] 2.1 `IReadModelPreservation` (`src/Stratara.Projections/Abstractions/IReadModelPreservation.cs`),
  documented member by member.
- [x] 2.2 `ReplayOutcome.ReadModelsRestored` and `ReplayCompletion.ReadModelsRestored` (`init`, `bool`) in
  `src/Stratara.Abstractions/Abstractions/Projections/IProjectionReplayState.cs`; the Redis state keeps it as a
  field of the outcome hash, the in-process state on the outcome record.
- [x] 2.3 `ProjectionReplayWorker`: preserve after `TryActivate` and renew the lease; restore on failure
  with `CancellationToken.None`; discard on success; nothing on interruption; `RestoreAbandonedAsync` once
  after subscribing — `tests/Stratara.Projections.Tests/Services/ProjectionReplayWorkerTests.cs`:
  `ReplayCallback_WithPreservation_PreservesBeforeClearingForgottenTenantsAndTruncating`,
  `ReplayCallback_WithPreservation_FailureRestoresAndCompletesWithReadModelsRestored`,
  `ReplayCallback_WithPreservation_SuccessDiscards`,
  `ReplayCallback_WithPreservation_CancellationLeavesTheCopy`,
  `ReplayCallback_PreservationFails_NothingIsTruncatedAndTheReplayFails`,
  `ReplayCallback_RestoreFails_CompletesWithoutReadModelsRestoredAndLogs`,
  `ExecuteAsync_WithPreservation_RestoresAnAbandonedCopyOnce`, `ExecuteAsync_WithoutPreservation_TouchesNothingAtStart`;
  without a registration every existing fact stays green unchanged.
- [x] 2.4 Log events in `LogEvents.Projection` — `ReadModelsPreserved` `104_021`, `ReadModelsRestored` `104_022`,
  `AbandonedReadModelsRestored` `104_023`, `PreservedReadModelsNotDiscarded` `104_025`, `ReadModelRestoreFailed`
  `104_124` — and their source-generated methods in `LoggerProjectionExtensions`.

## 3. The PostgreSQL implementation

- [x] 3.1 `ReadModelRestoreOptions` (`Schema`, `ExcludedTables`, `AdditionalTables`; section
  `ProjectionReplay:Restore`) and `AddReadModelRestore<TReadContext>()` in
  `src/Stratara.EventSourcing.EntityFrameworkCore/ReadStore/Replay/`;
  `tests/Stratara.Documentation.Tests/OptionsSectionBindingTests.cs` gets the section.
- [x] 3.2 Table set from the EF model (`PreservedTables.Of`) plus the `pg_constraint` checks (outside-in foreign
  key, cycle) and the insertion order — `tests/Stratara.EntityFrameworkCore.Tests/ReadStore/PreservedTablesTests.cs`:
  `TheSet_IncludesTheFrameworksOwnTables`, `TheSet_SkipsViewsKeylessTypesAndSqlQueries_AndNamesASharedTableOnce`,
  `TheSet_HonoursExcludedAndAdditionalTables`.
- [x] 3.3 `PreserveAsync` (repeatable-read snapshot, marker in the same transaction, existing marker kept and
  re-pointed), `RestoreAsync` (advisory lock, column comparison, truncate, parent-first insert with
  `OVERRIDING SYSTEM VALUE`, sequence reset, drop), `DiscardAsync`, `RestoreAbandonedAsync` — the facts of
  1.2 go green.

## 3b. Found in review

- [x] 3b.1 Round 1 of the review of #192 — `tests/Stratara.Orleans.IntegrationTests/Projections/ReplayRestoreTests.cs`:
  `A_reference_to_a_table_the_replay_does_not_empty_does_not_block_the_preservation` (false cycle),
  `A_copy_left_by_a_replay_that_succeeded_is_not_reused_for_the_next`, `A_copy_a_running_replay_owns_is_left_alone`
  (`StillOwned`, then restored once the marking ends); `ProjectionReplayWorkerTests`:
  `ExecuteAsync_WithPreservation_ChecksForAnAbandonedCopyBeforeTakingRequests`; the renewal during preparation and the
  thirty-second re-check are in `ProjectionReplayWorker` without a fact of their own (both wait on real time).

## 4. Documentation

- [x] 4.1 `docs/guides/write-a-projection.md`, *Replay is destructive, and it is all-or-nothing* (around
  line 298): the registration, what is copied and when, both endings, the dead-host restore, the queued-bundle
  window, disk and time cost, the `CREATE` permission, the unmapped-table caveat.
- [x] 4.2 `docs/guides/operate-the-orleans-execution-model.md`, *A rebuild or a replay that does not finish*
  (around line 338): checkpoints are part of the copy; restored readers resume from the restored positions.
- [x] 4.3 `docs/reference/di-extensions-cheatsheet.md`: a row for `AddReadModelRestore<TReadContext>()`.
- [x] 4.4 `CHANGELOG.md` → *Unreleased*.

## 5. Verify

- [x] 5.1 `openspec validate restore-the-read-models-when-a-replay-fails --strict`.
- [x] 5.2 `./scripts/local-gauntlet.sh` green.
- [ ] 5.3 `dotnet test tests/Stratara.Orleans.IntegrationTests` green (Docker): `ReplayRestoreTests` 9/9 (which caught a
  marker read on an unopened connection, fixed in the implementation), and the whole suite on the tip of the series.
