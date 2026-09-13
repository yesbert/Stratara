# Design — Detect a conflict on any provider

## Context

See `proposal.md` → *Why*. The state on `main` at `1476af9`, established for finding SF-003 of
`prove-an-orleans-execution-model` and confirmed by that change's test T7.

**The classification.** `EventSource.IsConcurrencyOrUniqueViolation`
(`src/Stratara.Infrastructure/EventSourcing/EventSource.cs:182-207`) returns true for
`ConcurrencyConflictException` (the unit-of-work wrap of EF's `DbUpdateConcurrencyException`), for
`DbUpdateConcurrencyException` itself, and for a `DbUpdateException` whose inner chain contains a
`PostgresException` with SQL state `23505` (`PostgresUniqueViolationSqlState`, line 45). The first
two never fire for an insert; a duplicate version is an insert. The third is the only branch that
recognises the real case, and it names a provider.

**How the provider gets there.** `Stratara.Infrastructure` has no `Npgsql` package reference
(`Stratara.Infrastructure.csproj`); `PostgresException` arrives transitively through
`Stratara.EventSourcing.EntityFrameworkCore`, which references `Npgsql.EntityFrameworkCore.PostgreSQL`.
The `using Npgsql;` in `EventSource.cs` is the whole dependency.

**The other provider that exists.** `Stratara.Testing.EntityFrameworkCore` references
`Microsoft.EntityFrameworkCore.Sqlite` and registers the write stack on SQLite through
`AddStrataraTestingEventStore` (`TestEventStoreServiceCollectionExtensions.cs`). SQLite refuses a
duplicate version with `SqliteException`, `SqliteErrorCode` 19 (`SQLITE_CONSTRAINT`) and
`SqliteExtendedErrorCode` 2067 (`SQLITE_CONSTRAINT_UNIQUE`) — or 1555
(`SQLITE_CONSTRAINT_PRIMARYKEY`) when the collision is on the key.

**The pin.** `tests/Stratara.Infrastructure.Tests/EventSourcing/EventSourceSqliteConcurrencyTests.cs`
asserts `DbUpdateException` and not `ConcurrencyException` on SQLite, and says in its summary that
it flips when this change lands.

**Where the registrations are.** `AddNpgsqlWriteDbContextFactory<TDbContext>()`
(`src/Stratara.EventSourcing.EntityFrameworkCore/EntityFrameworkCore/DependencyInjection/NpgsqlDbContextServiceCollectionExtensions.cs:56`)
already try-adds the unit of work for its context since
`let-a-store-context-bring-its-unit-of-work` (archived 2026-09-03) — the precedent that the context
registration is the one place that knows the provider. `AddEventSourcing()`
(`src/Stratara.Infrastructure/DependencyInjection/EventSourcingServiceCollectionExtensions.cs:33`)
registers `EventSource` and knows no provider.

## Goals / Non-Goals

**Goals:**

- `EventSource` names no provider.
- Each store registration brings the recognition of its provider's collision.
- A consumer with an unsupported provider can add theirs without replacing the framework's.
- The PoC's readers (`prove-an-orleans-execution-model`, constraint 3) can use the same port if
  they ever ship — the port is the one place that answers "was that a unique violation".

**Non-Goals:**

- Supporting a third provider. SQL Server or MySQL is a registration someone writes when they need
  it; the port makes it a small one.
- Reworking the two provider-neutral branches (`ConcurrencyConflictException`,
  `DbUpdateConcurrencyException`). They stay in `EventSource` as the provider-independent part of
  the classification; they are correct, just not sufficient.

## Decisions

### D1 — A port in `Stratara.Abstractions`, consulted as a set

```csharp
namespace Stratara.Abstractions.EventSourcing;

/// Recognises a database provider's refusal of a duplicate stream version.
public interface IStoreConflictDetector
{
    bool IsUniqueViolation(Exception exception);
}
```

`EventSource` takes `IEnumerable<IStoreConflictDetector>` and, for a `DbUpdateException` that is
not a `DbUpdateConcurrencyException`, asks each detector; any `true` makes the failure a
`ConcurrencyException`. Walking the inner-exception chain is the detector's job, because which
layer wraps the provider's exception is a provider detail (Npgsql nests it once; SQLite may not).

*Why a set and not one service.* One service resolved by "last registration wins" would make the
outcome depend on the order of `AddNpgsqlWriteDbContextFactory` and a consumer's registration —
and a test host that registers both PostgreSQL and SQLite contexts (the PoC's does) would lose
one. A set lets every registration add its own with `TryAddEnumerable`, so applying a registration
twice adds one detector, and a consumer adds theirs without knowing what is there.

*Alternative considered:* a `Func<Exception, bool>` on an options type. Not discoverable, not
composable, and a consumer cannot see what the framework registered.

*Alternative considered:* leave the detection in `EventSource` and add SQLite next to PostgreSQL.
Puts a second provider's package in `Stratara.Infrastructure` and answers the finding for exactly
one more provider; the third would be the same finding again.

Evidence: T7 in `prove-an-orleans-execution-model/evidence/results.md`; the pin test.

### D2 — Two implementations, each in the package that references its provider

- `PostgresConflictDetector` in `Stratara.EventSourcing.EntityFrameworkCore`: inner chain contains
  `PostgresException` with `SqlState == "23505"`. Registered by `AddNpgsqlWriteDbContextFactory`
  with `TryAddEnumerable`. The `using Npgsql;` leaves `EventSource.cs` with it, and
  `Stratara.Infrastructure` no longer compiles against a provider type.
- `SqliteConflictDetector` in `Stratara.Testing.EntityFrameworkCore`: inner chain contains
  `SqliteException` with `SqliteErrorCode == 19` and extended code 2067 or 1555. Registered by
  `AddStrataraTestingEventStore` with `TryAddEnumerable`.

Both are `internal sealed`; the port is public, the implementations are not the consumer's
vocabulary.

### D3 — No detector registered means PostgreSQL semantics are not assumed

A host that registers `AddEventSourcing()` with a context registration of its own (not the
framework's) gets an empty set and therefore only the provider-neutral branches — a collision would
surface as `DbUpdateException`. That is the SQLite behaviour of today generalised, and it is
honest: the framework does not know the provider. The XML docs on `AddEventSourcing` say that the
store registration is what brings the detector, and the guide on custom contexts says the same.

*Alternative considered:* fall back to the PostgreSQL detector when the set is empty. Would keep a
provider in `Stratara.Infrastructure` and hide the missing registration.

## Risks / Trade-offs

- **A consumer who registered the write context without the framework's registration loses the
  PostgreSQL recognition they had.** → Only if they bypassed `AddNpgsqlWriteDbContextFactory`, which
  since 2026-09-03 is the documented registration; the changelog entry names the case and the
  one-line fix (register the detector or use the registration).
- **SQLite's extended error code is only populated when the connection was opened with extended
  result codes.** → `Microsoft.Data.Sqlite` enables them; the detector also accepts the primary
  code 19 with a message containing `UNIQUE`, and the pin test flipped in task 3.1 proves the path.
- **The detectors walk the exception chain on every `DbUpdateException`.** → Only on the failure
  path; a save that succeeds never sees them.

## Migration Plan

1. Ship. A host on `AddNpgsqlWriteDbContextFactory` sees no difference; a test on the SQLite host
   now gets `ConcurrencyException` where it got `DbUpdateException` — a behaviour change only in
   test-support, called out in the changelog.
2. Rollback: none needed; the previous classification is a subset of the new one on PostgreSQL.

## Open Questions

None.
