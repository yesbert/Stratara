# Detect a conflict on any provider

> **Status:** approved

## Why

A duplicate stream version is an insert that violates the store's unique index, and the event source
recognises that only through the PostgreSQL unique-violation state. On any other provider the same
collision surfaces as a plain update exception, and everything keyed on the concurrency exception —
the retry pipeline, the requeue on the bus — behaves differently. The test-support package runs the
same write stack on SQLite, where it is now pinned: `EventSourceSqliteConcurrencyTests` shows a
`DbUpdateException` where PostgreSQL gives `ConcurrencyException`.

Recorded as finding SF-003 of change `prove-an-orleans-execution-model`, confirmed there by test T7,
and decided by the owner on 2026-09-13: put the detection behind a provider port selected by the
store registration, the way the transport is selected.

## What Changes

- A duplicate stream version surfaces as a concurrency conflict on every provider the store supports,
  not only on PostgreSQL.
- The classification of a provider's unique-violation error lives behind a port that the store
  registration selects; the PostgreSQL and SQLite implementations ship, a consumer may register one
  for another provider.
- The SQLite pin test flips to expecting `ConcurrencyException`.

No API is removed. A consumer that only ever ran on PostgreSQL sees no difference.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `event-sourcing-store`: *A concurrency conflict discards the batch and is distinguishable* gains
  the scenario that the conflict is distinguishable on every supported provider; *Registering the
  store's context makes the store usable* says the registration brings the recognition and that a
  consumer may add one for a provider the framework does not ship.

## Impact

- `src/Stratara.Infrastructure/EventSourcing/EventSource.cs` — `IsConcurrencyOrUniqueViolation`
  consults the port; the `using Npgsql;` goes, and `Stratara.Infrastructure` no longer compiles
  against a provider type (the package reference was always transitive).
- `src/Stratara.Abstractions/` — the port, `IStoreConflictDetector`.
- `src/Stratara.EventSourcing.EntityFrameworkCore/` — the PostgreSQL implementation, registered by
  `AddNpgsqlWriteDbContextFactory`.
- `src/Stratara.Testing.EntityFrameworkCore/` — the SQLite implementation, registered by
  `AddStrataraTestingEventStore`.
- `tests/Stratara.Infrastructure.Tests/EventSourcing/EventSourceSqliteConcurrencyTests.cs` — flips.
- Superseded sources: none.
