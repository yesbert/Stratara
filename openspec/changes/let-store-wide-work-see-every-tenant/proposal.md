# Let the framework's store-wide work see every tenant

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

## What Changes

- The framework's work across the store ignores the write context's query filters. That covers both commit-order
  readers and the portable reader's start check, the replay's reads, and the hash chain: its reads of unhashed and
  preceding entries, of the last hashed entry, and of its anchors.
- A read about one stream keeps the consumer's filters: existence, first entry, version, the stream's entries and its
  snapshots. Those run under a session and are the reads the filter exists for.
- The store-wide members of `IEventStreamRepository` and `IEventChainRepository` document that they read every
  tenant's entries.
- The tenant-isolation guide says what a filter on the write context reaches and what it does not.

## Capabilities

### New Capabilities

### Modified Capabilities

- `tenant-isolation`: *Tenant-scoped rows are filtered at the database as well as at the entrance*. The framework's
  own work across the store sees every tenant's entries whatever filters the write context declares, and a query
  about one stream keeps them.

## Impact

- `Stratara.EventSourcing.EntityFrameworkCore`: `EventStreamRepository` (store-wide members), `EventChainRepository`.
- `Stratara.Orleans.EntityFrameworkCore`: `PortableCounterReader`, `PortableCounterStartupCheck`,
  `PostgresTransactionIdReader`.
- `Stratara.Abstractions`: remarks on `IEventStreamRepository` and `IEventChainRepository`.
- `docs/guides/enforce-tenant-isolation.md`.
- Tests: `Stratara.WriteStore.Tests`, `Stratara.Testing.Orleans.Tests`, `Stratara.Orleans.IntegrationTests`.
- `CHANGELOG.md` under the open `[4.4.0]`.
