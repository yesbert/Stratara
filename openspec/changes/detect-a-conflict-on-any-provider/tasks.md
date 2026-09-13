Ordered so that the port exists before either implementation, the implementations before the
event source stops naming a provider, and the pin test flips last — its flip is the proof.

## 1. The port

- [x] 1.1 `src/Stratara.Abstractions/Abstractions/EventSourcing/IStoreConflictDetector.cs` (new):
      `bool IsUniqueViolation(Exception exception)`, XML docs in house style saying who registers
      one and that the framework consults every registered detector. Verify: the public-API surface
      test in `tests/Stratara.Abstractions.Tests` (if present) lists it; `dotnet build` clean.
      *Done 2026-09-13: no such test project exists; the build is the verification.*

## 2. The two implementations

- [x] 2.1 `src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/PostgresConflictDetector.cs`
      (new, `internal sealed`): walks the inner chain for `PostgresException` with SQL state
      `23505`. `AddNpgsqlWriteDbContextFactory<TDbContext>()` registers it with
      `TryAddEnumerable`. Verify: `tests/Stratara.EntityFrameworkCore.Tests/DependencyInjection/NpgsqlDbContextServiceCollectionExtensionsTests.cs`
      — registering twice yields one detector; a constructed `DbUpdateException` wrapping a
      `PostgresException` with state `23505` is recognised, with another state is not.
- [x] 2.2 `src/Stratara.Testing.EntityFrameworkCore/SqliteConflictDetector.cs` (new,
      `internal sealed`): `SqliteException` with primary code 19 and extended code 2067 or 1555, or
      primary code 19 with `UNIQUE` in the message. `AddStrataraTestingEventStore` registers it
      with `TryAddEnumerable`. Verify: a unit test in `tests/Stratara.Testing.Tests` (or the slice
      that covers the EF test host) for the three accepted shapes and one rejected.

## 3. The event source

- [x] 3.1 `src/Stratara.Infrastructure/EventSourcing/EventSource.cs`: take
      `IEnumerable<IStoreConflictDetector>`; `IsConcurrencyOrUniqueViolation` keeps the two
      provider-neutral branches and asks the detectors for any other `DbUpdateException`; delete
      `PostgresUniqueViolationSqlState` and `using Npgsql;`. `AddEventSourcing` XML docs say the
      store registration brings the detector (design D3). Verify:
      `tests/Stratara.Infrastructure.Tests/EventSourcing/EventSourceSqliteConcurrencyTests.cs`
      flips to `ConcurrencyException` — rename the test to say so and rewrite its summary to record
      that SF-003 is closed here; `grep -rn Npgsql src/Stratara.Infrastructure --include=*.cs`
      returns nothing.
- [x] 3.2 A test with an empty detector set on the SQLite host (register the context without
      `AddStrataraTestingEventStore`'s detector) shows `DbUpdateException` — pinning design D3 so
      nobody adds a silent fallback later. Verify: the test is next to 3.1 and names D3.

## 4. Documentation and changelog

- [x] 4.1 `docs/reference/di-extensions-cheatsheet.md`: the write-factory row and the test-store row
      mention the detector; the guide that describes bringing one's own context (find it with
      `grep -rln "AddNpgsqlWriteDbContextFactory" docs`) says a custom provider registers an
      `IStoreConflictDetector`. Verify: both files name the interface.
      *Done 2026-09-13: no guide under `docs/` names the registration besides the cheatsheet and the
      generated API pages, so the cheatsheet's two rows carry the note.*
- [x] 4.2 Regenerate `llms-full.txt` with the generator the documentation tests use. Verify: the
      documentation tests pass. *Done 2026-09-13: regenerated without a diff — the catalogue carries
      the registration summaries, and only remarks changed.*
- [x] 4.3 `CHANGELOG.md` `[Unreleased]` → *Added*: `IStoreConflictDetector`, registered by the two
      store registrations; → *Fixed*: a version collision on SQLite is now a `ConcurrencyException`
      (test-support behaviour change); → the note for hosts that bypassed the registration. Verify:
      the entry names the one-line fix for such hosts.

## 5. Gate

- [x] 5.1 `./scripts/local-gauntlet.sh` green; `openspec validate detect-a-conflict-on-any-provider --strict`
      clean.
