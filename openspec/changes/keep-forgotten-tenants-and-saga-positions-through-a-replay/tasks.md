# Tasks

## 1. Pass a forgotten tenant's facts over before handling

- [x] 1.1 Write failing tests in `tests/Stratara.Projections.Tests/Services/ProjectionHandlerForgottenTenantTests.cs`: `TenantDeleted` then `EntryCreated` of that tenant on another stream leaves no row, the projection is not called, and `104_014` is logged once. The same for `CustomerTenantsDeleted`, and with the deletion in an earlier `ProjectAsync` call. Confirm red and keep the output.
- [x] 1.2 Write a failing test in `tests/Stratara.Projections.Tests/Services/ProjectionReplayWorkerTests.cs` beside the existing forgotten-tenant replay test (the `EntryProjection` near line 898): a replay over a deletion followed by a row-adding fact completes with no row of that tenant. Confirm red.
- [x] 1.3 Add tests to `ProjectionHandlerForgottenTenantTests`: a deletion fact of an already recorded tenant is still handled; a fact with an empty tenant is handled with `HasForgottenAsync` never called; one read per fact (`Times.Exactly`), with a deletion applied between two facts seen by the later one; a fact of a recorded tenant whose handler would remove another tenant's row is passed over and the row stays; `A_projection_that_does_not_declare_itself_applies_a_deleted_tenants_fact_without_asking_the_store`.
- [x] 1.4 Implement in `src/Stratara.Projections/Services/ProjectionHandler.cs`: the check before handling, the exemptions and the shared store-failure wrapping. `A_store_that_fails_before_handling_keeps_the_projection_from_being_called` proves the projection was not called for a row-adding fact. Verify `dotnet test tests/Stratara.Projections.Tests` is green.
- [x] 1.5 Add a test to `tests/Stratara.Testing.Orleans.Tests/ForgottenTenantOnTheExecutionModelTests.cs`: a store-reading projection meets the deletion, then a row-adding fact of that tenant on another stream, and holds no row. Verify `dotnet test tests/Stratara.Testing.Orleans.Tests` is green, and red without 1.4.
- [x] 1.6 Update the XML docs of `IForgetsDeletedTenants` and the `ProjectionHandler` remarks, including the limit on removals elsewhere. Update `docs/guides/write-a-projection.md` (the section "A deleted tenant's late facts": replace "applied as usual", state the read per fact and the limits), `docs/guides/tenant-membership.md` and `src/Stratara.Projections/README.md`. Verify with `dotnet test tests/Stratara.Documentation.Tests`.

## 2. Correct the truncator's contract

- [x] 2.1 Rewrite the `IProjectionViewTruncator` XML summary in `src/Stratara.Abstractions/Abstractions/Projections/IProjectionReplayState.cs` per design decision 8, and update `docs/guides/write-a-projection.md` and `docs/guides/migrate-to-the-orleans-execution-model.md`. Verify `grep -rn 'TRUNCATE' src docs` no longer recommends a cascade, and `dotnet test tests/Stratara.Documentation.Tests` is green.

## 3. Guard the saga positions through a replay

- [x] 3.1 Add `IProjectionCheckpointStore.ListAsync` (default throws `NotSupportedException`) and the `StoredCheckpoint` record, documented with `[ExcludeFromCodeCoverage]` on the record, and override it in `ProjectionCheckpointStore<TContext>`. Prove with `Listing_*` tests in `tests/Stratara.Orleans.Tests/ProjectionCheckpointStoreTests.cs` (in-memory provider) and `ProjectionCheckpointGuardTests.cs` (SQLite): rows of every reader and partition are returned, another prefix is not, and an empty result is an empty list.
- [x] 3.2 Add `ReplaySagaCheckpointsRestored`, `ReplaySagaCheckpointsNotGuarded` and `ReplaySagaCheckpointsNotRestored` to `src/Stratara.Diagnostics/LogEvents.cs` and `src/Stratara.Orleans/Diagnostics/OrleansLog.cs`, and list them in `docs/reference/log-events-schema.md`. Verify `dotnet test tests/Stratara.Documentation.Tests` passes `LogEventAllocationTests`.
- [x] 3.3 Write failing tests in `tests/Stratara.Orleans.Tests/ReplayCheckpointResetTests.cs`: a truncator that empties an in-test store leaves the saga positions intact and logs the warning naming the truncator. Confirm red before 3.5.
- [x] 3.4 Add tests to `ReplayCheckpointResetTests` for: the journal order (list before the first reset); a truncator that touches nothing (no write, no warning); a position at a lower value raised, and a further one left; a row under another reader left; the `sagas` shared row kept and a projection named `sagasFoo` not; a failing truncator (written back, the truncator's failure stands); both failing (`AggregateException`); a failing first list (truncator not called, nothing reset, readers resumed); a failing write-back (error logged, replay fails); a store without `ListAsync` (replay runs, warning); a cancelled token during the truncation (still written back).
- [x] 3.5 Implement the guard in `src/Stratara.Orleans/Projections/ReplayCheckpointReset.cs` per design decisions 5 and 6, and update the class's summary. Verify `dotnet test tests/Stratara.Orleans.Tests` is green and the existing `ReplayCheckpointResetTests` still pass unchanged.
- [x] 3.6 Add an integration test to `tests/Stratara.Orleans.IntegrationTests/Projections/ReplayWithStoreReadersTests.cs` on PostgreSQL: a truncator that empties the checkpoint table during a full replay leaves the saga rows at their values and the projections' refilled. Verify with `--filter-class`, after checking `uptime` (wait if the 1-minute load is above 8).
- [x] 3.7 Document the guard's coverage and limits in `docs/guides/write-a-projection.md` and `docs/guides/migrate-to-the-orleans-execution-model.md`, and the optional member on `IProjectionCheckpointStore`. Verify `dotnet test tests/Stratara.Documentation.Tests` is green.

## 4. Integration checks

- [x] 4.1 Run `openspec validate keep-forgotten-tenants-and-saga-positions-through-a-replay --strict` and verify it passes.
- [x] 4.2 Run `dotnet test` one project at a time on `tests/Stratara.Projections.Tests`, `tests/Stratara.Orleans.Tests`, `tests/Stratara.Testing.Orleans.Tests`, `tests/Stratara.Documentation.Tests`, with output to a file and grepped. Record the counts for the report.
- [x] 4.3 Run the touched integration classes `ReplayWithStoreReadersTests` and `ReplayRestoreTests` singly with `--filter-class` after the `uptime` check, and record the counts.

## Workflow follow-up

- No commit, push, archive or `CHANGELOG.md` edit in this work: the diff is reviewed uncommitted.
- Before a release, the note on the reversal of "applied as usual" goes into `CHANGELOG.md`.
