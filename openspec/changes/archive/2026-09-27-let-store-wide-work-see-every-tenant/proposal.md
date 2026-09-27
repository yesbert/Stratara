# Let the framework's work on its store see every tenant

> **Status:** approved

## Why

The framework declares its own store entities tenant-scoped: event entries, snapshots, hash-chain anchors and the
command log. The tenant-isolation guide switches the database filter on with `ApplyGlobalTenantQueryFilters(this)`,
which filters every tenant-scoped entity of the context it is called on, and nothing says not to call it on the
write context. Called there, it also filters the framework's own work across the store. That work runs without a
session, so the filter matches no tenant, and it silently sees nothing:

- The commit-order readers return no entries.
- The portable reader's start check passes although unpositioned entries exist.
- A replay empties the read models and applies nothing.
- The hash chain hashes nothing.

The portable backfill had the same defect and is fixed by `read-and-prepare-a-long-history-in-linear-time`. The owner
decided on 2026-09-27 that the rest is a bug to fix before 4.4.0, not a configuration to forbid or merely document.

The independent review then showed that the framework also reads single streams without a session. A saga
process's liveness check and its timeout both do this, so the filter reported live processes as dead and dropped
their timers. A stream may also hold events of two owners (`AppendOnBehalfOfAsync`). Under one owner's session such
a stream was half read, snapshotted wrong for good, and refused every later append with a `ConcurrencyException`.
The owner decided on 2026-09-27 that the framework reads its store past the filters everywhere.

## What Changes

- Every read the framework makes of its own store ignores the write context's query filters: event entries,
  snapshots and hash-chain anchors, whether across the store or about one stream. That covers the event stream and
  snapshot repositories, both commit-order readers and the portable reader's start check, and the hash chain's anchor
  read. A filter the consumer declares on the write context applies to the consumer's own queries only.
- **Behaviour change** for a consumer whose write context filters by tenant. That write context now behaves like
  one that declares no filter. A command handler given another tenant's aggregate id loads that stream and appends
  to it, recording the event for the stream's owner with the session's actor, as every unfiltered write context
  always has. Before, the filter made the load come back empty and the append fail with a version conflict. The
  repositories (`IEventStreamRepository`, `ISnapshotRepository`, `IEventChainRepository`) return every tenant's
  rows to a caller that uses them directly.
- The three repository contracts document it.
- The tenant-isolation guide says what a filter on the write context reaches. It also says that neither the
  entrance guard nor the filter checks a stream's owner against the session, so a handler that must refuse another
  tenant's stream checks the loaded aggregate's owner. And it says that a view truncator and a rebuildable
  projection's truncation run without a session.
- The owner confirmed on 2026-09-27, with that behaviour stated, that the framework reads unfiltered and documents
  it honestly.

## Capabilities

### New Capabilities

### Modified Capabilities

- `tenant-isolation`: *Tenant-scoped rows are filtered at the database as well as at the entrance*. The framework's
  reads of its own store see every entry whatever filters the write context declares. The filter applies to the
  consumer's own queries.

## Impact

- `Stratara.EventSourcing.EntityFrameworkCore`: `EventStreamRepository`, `SnapshotRepository`, `EventChainRepository`.
- `Stratara.Orleans.EntityFrameworkCore`: `PortableCounterReader`, `PortableCounterStartupCheck`,
  `PostgresTransactionIdReader`.
- `Stratara.Abstractions`: remarks on `IEventStreamRepository`, `ISnapshotRepository` and `IEventChainRepository`.
- `docs/guides/enforce-tenant-isolation.md`.
- Tests: `Stratara.WriteStore.Tests`, `Stratara.Testing.Orleans.Tests`, `Stratara.Testing.EntityFrameworkCore.Tests`,
  `Stratara.Orleans.IntegrationTests`.
- `CHANGELOG.md` under the open `[4.4.0]`.
