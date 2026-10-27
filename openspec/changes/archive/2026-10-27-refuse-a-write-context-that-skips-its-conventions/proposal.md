# Refuse a write context that skips its conventions

> **Status:** approved

## Why

Release 4.5.0 declares the indexes on the event stream's sequence number from `WriteDbContext<TContext>.ConfigureConventions`.
A derived write context that overrides `ConfigureConventions` without calling `base` loses those indexes, and nothing
reports it. The migration looks clean, and the hashing worker and the sequence-order reads stay sequential scans on a
large store, which is the defect 4.5.0 exists to fix. Overrides of that kind are common: Entity Framework's own
`DbContext.ConfigureConventions` is empty, so an override has no reason to call it. Until 4.5.0, `WriteDbContext`
did not override it either. The review of both pull requests that carry 4.5.0 (#224, #225) raised this, and a
sentence in the release notes is the only defence so far.

## What Changes

The effect a consumer sees: a write context that overrides `ConfigureConventions` without calling `base` fails when
its model is built. It fails with an `InvalidOperationException` that names the context and says to call
`base.ConfigureConventions`. This happens at the first use at runtime, and when `dotnet ef` builds the model for a
migration. It no longer runs on a model without the indexes.

- **Fails loudly instead of degrading silently.** Building the model of such a context throws before any query runs
  and before a migration is generated.
- **Unchanged:** a context that calls `base.ConfigureConventions`, or does not override it at all, which covers both
  consumers known today. The indexes, their names, the reads and every public type and member are unchanged.
- **Release:** this lands in 4.5.0 before the version is tagged. The 4.5.0 CHANGELOG section gets the entry, and its
  upgrade note on `ConfigureConventions` says that the model is refused.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `event-sourcing-store`: *The store declares its own schema* gains that a write context which keeps the
  framework's conventions from running is refused when its model is built, rather than migrating without the
  indexes.

## Impact

- **Code:** `Stratara.EventSourcing.EntityFrameworkCore`, `WriteDbContext<TContext>`. Its `ConfigureConventions`
  records that it ran, and its `OnModelCreating` throws when it did not. No public type, member or option changes.
- **Tests:** `tests/Stratara.EntityFrameworkCore.Tests/StoreSchemaAdditionsTests.cs`. The test that pinned the
  missing indexes now asserts the refusal and its message. The test context that simulated a bridged consumer by
  skipping `base` now calls it and declares its own indexes beside the framework's.
- **Documentation:** the XML remarks of `WriteDbContext<TContext>`, the schema section of
  `docs/getting-started/di-composition.md`, and the 4.5.0 section of `CHANGELOG.md`.
- **Release:** part of 4.5.0, which is not yet tagged.
- **Superseded sources:** none.
