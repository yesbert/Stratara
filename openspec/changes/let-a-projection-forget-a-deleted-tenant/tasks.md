## 1. Contracts

- [x] 1.1 Add `IForgetsDeletedTenants : IProjection` (marker) and `IForgottenTenantStore` (`ForgetAsync`,
  `HasForgottenAsync`, `ClearAsync`) under `src/Stratara.Projections/Abstractions/`, with full XML
  documentation.
- [x] 1.2 Add `ProjectionForgottenTenantFactPassedOver = 104_014` to `src/Stratara.Diagnostics/LogEvents.cs`
  and its source-generated log method in `src/Stratara.Projections/Diagnostics/Extensions/`.

## 2. The handler, test first

- [x] 2.1 Add `tests/Stratara.Projections.Tests/Services/ProjectionHandlerForgottenTenantTests.cs` with an
  in-memory `IForgottenTenantStore` and a declaring projection that removes a tenant's rows on deletion
  and throws `PrecedingFactMissingException` for a fact whose row is gone. Cases:
  - a late fact after `CustomerTenantsDeleted` is passed over and the next event still runs;
  - the same after `TenantDeleted`, which records the stream id;
  - every tenant of the cascade is recorded;
  - a declaring projection without a deletion handler is still handed the deletion facts, and
    `GetRelevantEventTypeNames` includes them;
  - a missing prerequisite for an unrecorded tenant propagates;
  - a non-declaring projection is untouched: no extra relevant types, and the exception propagates;
  - a declaring projection with no store registered fails, naming both registrations;
  - a deletion whose own handler throws records nothing.
- [x] 2.2 Implement it in `src/Stratara.Projections/Services/ProjectionHandler.cs`, with the store as an
  optional constructor parameter and an `ILogger<ProjectionHandler>`. Run 2.1 green.
- [x] 2.3 Add a replay-level case to `tests/Stratara.Projections.Tests/Services/ProjectionReplayWorkerTests.cs`:
  a history with a creation, the cascade and a late fact replays to completion through the real
  manager and handler.

## 3. Discovery

- [x] 3.1 In `AddProjectionsFromAssemblyContaining<T>`
  (`src/Stratara.Projections/DependencyInjection/ProjectionServiceCollectionExtensions.cs`), register
  `TenantDeleted` and `CustomerTenantsDeleted` for a type implementing `IForgetsDeletedTenants`. Add a
  test in `tests/Stratara.Projections.Tests` asserting both resolve after scanning a declaring
  projection that handles neither.

## 4. The read store

- [x] 4.1 Add `ForgottenTenant` and its configuration (table `projection_forgotten_tenant`, key on
  projection and tenant, projection up to 255 characters) under
  `src/Stratara.EventSourcing.EntityFrameworkCore/ReadStore/ForgottenTenants/`, and
  `ForgottenTenantStore<TContext>` implementing `IForgottenTenantStore`, as `design.md` describes.
- [x] 4.2 Register the store in `AddNpgsqlReadDbContextFactory<TDbContext>` with `TryAddScoped`, and add
  `AddStrataraForgottenTenants<TReadContext>()`.
- [x] 4.3 Tests in `tests/Stratara.EntityFrameworkCore.Tests`:
  - `ReadDbContextTests` asserts the entity is declared;
  - `StoreSchemaAdditionsTests` asserts the table and its key;
  - `ForgottenTenantStoreTests`, on SQLite, covers forget idempotently (twice, and overlapping lists),
    has-forgotten per projection, and clear one projection keeping the others;
  - a registration test asserts that `AddNpgsqlReadDbContextFactory` provides the store.

## 5. Clearing

- [x] 5.1 `ProjectionReplayWorker.RunReplayAsync` empties the record of each registered declaring
  projection before `TruncateAllAsync`. Cover it in `ProjectionReplayWorkerTests`: the declaring
  projection's record is cleared, before the truncation, and only its record; a host without a
  declaring projection does not touch the store.
- [x] 5.2 `ProjectionRebuilder` clears a declaring projection's record just before its truncation,
  between the resets. Cover it in the existing rebuilder tests in `tests/Stratara.Orleans.Tests`,
  together with a non-declaring projection that leaves the store alone.
- [x] 5.3 `src/Stratara.Testing.Orleans/ExecutionModelTestHost.cs` registers
  `AddStrataraForgottenTenants<StrataraTestReadDbContext>()`.

## 6. Documentation

- [x] 6.1 `docs/guides/write-a-projection.md`: a section on forgetting a deleted tenant. It covers the
  declaration, what is passed over and what is not, the migration, the replay after upgrading, and the
  hand-registered projection's trusted types.
- [x] 6.2 `docs/guides/tenant-membership.md`: link to it from the deletion paragraph.
  `docs/reference/di-extensions-cheatsheet.md`: the new registration and the store on
  `AddNpgsqlReadDbContextFactory`. `src/Stratara.Projections/README.md`: one line.
- [x] 6.3 Add the entry to `CHANGELOG.md` under Unreleased: Added, and an Upgrading note for the
  migration and the replay.

## 7. Review follow-ups

Copilot could not review (the requester's quota was exhausted), so an independent review ran on the
branch. What it found, and what was done:

- [x] 7.1 The replay emptied every projection's record in the read store, including another
  deployment's, and needed the table even in a host without a declaring projection, failing after the
  truncation. It now clears only the host's declaring projections, by name, before the truncation.
  `ClearAllAsync` is gone from the contract. Covered by
  `ReplayCallback_EmptiesTheRecordOfEachDeclaringProjectionBeforeTruncating` and
  `ReplayCallback_WithoutADeclaringProjection_DoesNotTouchTheStore`.
- [x] 7.2 On the Orleans model the replay's clear ran after the readers resumed. Clearing before the
  truncation, in the replay and in the rebuild, removes the window (see `design.md`).
- [x] 7.3 Two requirements contradicted the new one after archiving. The delta now modifies *A
  projection declares the events it cares about by handling them* and *A projection can report that a
  fact's prerequisite has not been applied yet*.
- [x] 7.4 A partial insert conflict in `ForgetAsync` rethrew. It now re-inserts the missing rows, up
  to three attempts.
- [x] 7.5 A failing store replaced the `PrecedingFactMissingException` and lost its retry. It now
  surfaces as one, carrying the store's failure. Covered by
  `A_store_that_fails_is_reported_as_the_missing_prerequisite`.
- [x] 7.6 The declaration is a promise about both deletion facts. It is stated on the marker, in the
  requirement and in the guide, whose example now handles both.
- [x] 7.7 The documentation offered a projection's rebuild as a general alternative to a replay, but it
  exists only on Orleans for `IRebuildableProjection`. It also said projections cannot race each other,
  which holds between projections only. Both corrected.
- [x] 7.8 There was no test on the Orleans path. Added
  `tests/Stratara.Testing.Orleans.Tests/ForgottenTenantOnTheExecutionModelTests.cs`: the store reader
  reads past the late fact, and without the declaration the partition stalls. The spec scenarios now
  say what they were verified on.

## 8. Gate

- [x] 8.1 `openspec validate let-a-projection-forget-a-deleted-tenant --strict`
- [x] 8.2 `./scripts/local-gauntlet.sh`
