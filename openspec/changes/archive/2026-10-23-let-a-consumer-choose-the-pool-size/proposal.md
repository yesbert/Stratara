# Let a consumer choose the pool size

> **Status:** approved

## Why

The PostgreSQL registrations of the write, read and identity contexts rewrite every connection string whose
maximum pool size is Npgsql's default of 100 to 15. They compare the parsed value, not whether the consumer named
one, so a host that writes `Maximum Pool Size=100` on purpose gets 15 as well, and 101 is the smallest pool above 15 a
consumer can choose. Nothing logs the rewrite, and no guide or specification mentions it. A consumer found it from
an exception (`The connection pool has been exhausted, either raise 'Max Pool Size' (currently 15)`) on a silo where
every role shares those connections (consumer finding F-026, reported 2026-10-23).

The default itself has a reason: one host registers several contexts over one database, and several hosts share
that database's `max_connections`. That reason belongs in a guide, beside the decision, not in a silent rewrite.

## What Changes

The consumer-visible effect is that the pool size a connection string names is the pool size the context uses.

- **A named pool size is kept.** The registrations apply 15 only where the connection string names no maximum pool
  size. They decide this from the keys of the string the host gave, not from the parsed value.
  `Maximum Pool Size=100` keeps 100.
- **The pool size in effect is logged.** When a host creates the first context for a database, it logs the maximum
  pool size and whether it came from the connection string or the framework's default. It logs once per host and
  database, never with the password (Information).
- **Documented.** *Getting started — DI composition*, the operations guide of the Orleans execution model and the
  DI cheatsheet say what the default is, why it is 15, that contexts sharing a connection string share its pool, and
  that every role of a silo draws from it.
- **Unchanged:** a connection string without a pool size still gets 15, and one with any other value keeps it.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `event-sourcing-store`: gains *The connection pool size is the consumer's to choose*.

## Impact

- `src/Stratara.EventSourcing.EntityFrameworkCore/EntityFrameworkCore/DependencyInjection/NpgsqlDbContextServiceCollectionExtensions.cs`:
  `EnsureMaxPoolSize` decides from the connection string's keys. `PoolSizeReport.cs` (new, internal) is the per-host
  record of what was logged.
- `src/Stratara.Diagnostics/LogEvents.cs`: one new event id, `102_008`. `docs/reference/log-events-schema.md` lists
  the `102_000` range only and needs no row.
- `docs/getting-started/di-composition.md`, `docs/guides/operate-the-orleans-execution-model.md`,
  `docs/reference/di-extensions-cheatsheet.md`, `CHANGELOG.md`.
- Behaviour change for exactly one input: a connection string that names `Maximum Pool Size=100` now gets 100 instead
  of 15. No API, package, dependency, tier or schema change. A patch release (4.4.7).
- Dissolved or superseded legacy sources: none.
