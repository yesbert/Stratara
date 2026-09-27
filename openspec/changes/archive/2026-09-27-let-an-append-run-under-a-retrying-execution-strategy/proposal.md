# Let an append run under a retrying execution strategy

> **Status:** approved

## Why

A host that maintains the partition counter for the portable commit-order reader cannot append a single
event once its write context retries on failure — EF's `EnableRetryOnFailure`, which Aspire's EF
integrations switch on by default. The counter's interceptor begins a transaction of its own when a save
starts, outside the context's execution strategy, and EF refuses a transaction begun that way under a
retrying strategy: every save fails with "The configured execution strategy … does not support
user-initiated transactions". A probe on SQLite with a retrying strategy shows it. The owner asked on
2026-09-26 that every known bug be fixed before 4.4.0; this one was found while closing that release's
review.

## What Changes

- A save through the framework's unit of work on a relational context whose execution strategy retries on
  failure runs as one retriable unit through that strategy: it begins a transaction, writes its changes,
  commits without the caller's token and only then accepts them — the pattern EF documents for a retrying
  strategy. The partition counter's interceptor finds that transaction and no longer opens its own; a
  transient failure runs the whole unit again, with fresh positions.
- A context whose execution strategy does not retry, or a save that already runs inside a transaction —
  one the caller began, or an ambient one — keeps today's path unchanged.

## Capabilities

### New Capabilities

### Modified Capabilities
- `orleans-execution`: *Projections and sagas read the store in commit order and never miss a committed
  fact* — appending, and positioning every append, works whatever execution strategy the write context
  uses, a retrying one included.

## Impact

- `src/Stratara.EventSourcing.EntityFrameworkCore/EntityFrameworkCore/UnitOfWork.cs` — the transaction's save
  runs as a retriable unit under a retrying execution strategy.
- `tests/Stratara.Testing.Orleans.Tests` — an append on SQLite with a retrying strategy and the partition
  counter's interceptor; `tests/Stratara.EntityFrameworkCore.Tests` — the unit under a retrying strategy, and
  the unchanged path without one.
- `docs/guides/migrate-to-the-orleans-execution-model.md` (the portable reader) and `CHANGELOG.md`.
- No published behaviour changes for a context whose strategy does not retry.
