## 1. Contracts

- [x] 1.1 Add `IForgetsDeletedTenants : IProjection` (marker) and `IForgottenTenantStore` (`ForgetAsync`,
  `HasForgottenAsync`, `ClearAsync`, `ClearAllAsync`) under `src/Stratara.Projections/Abstractions/`,
  with full XML documentation.
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
    has-forgotten per projection, clear one projection keeping the others, and clear all;
  - a registration test asserts that `AddNpgsqlReadDbContextFactory` provides the store.

## 5. Clearing

- [x] 5.1 `ProjectionReplayWorker.RunReplayAsync` clears every record after `TruncateAllAsync` when a
  store is registered. Cover it in `ProjectionReplayWorkerTests`: clear called after truncation, and
  a replay without a store runs as before.
- [x] 5.2 `ProjectionRebuilder` clears the rebuilt projection's record together with its truncation.
  Cover it in the existing rebuilder tests in `tests/Stratara.Orleans.Tests`.
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

## 7. Gate

- [x] 7.1 `openspec validate let-a-projection-forget-a-deleted-tenant --strict`
- [ ] 7.2 `./scripts/local-gauntlet.sh`
