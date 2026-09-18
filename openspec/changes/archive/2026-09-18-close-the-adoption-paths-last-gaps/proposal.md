# close-the-adoption-paths-last-gaps

> **Status:** approved (owner, 2026-09-17 — recorded at the owner's request)

## Why

The last findings of the review that replaced Copilot's on the pull requests merged on 2026-09-17,
and they are all on the path a populated store takes into the execution model.

- **The backfill walks the whole table once per batch.** Every batch asks for the oldest entries
  without a commit record, and nothing indexes "without a commit record": ten million entries in
  batches of a thousand is ten thousand scans, each over everything the batches before it stamped.
  The migration it belongs to is a window in which nothing may append, so its length is the outage.
- **The partition counter's backfill holds a partition's whole history in memory and its counter lock
  for the whole run**, for the same kind of store.
- **The native reader's head is held back by an open write transaction**, which the head's own
  contract does not say. A deployment that seeds while its old hosts still append therefore applies
  what they commit in that window a second time, quietly.
- **A singleton work whose run throws a cancellation nobody asked for is not logged.** The catch
  filter skips every `OperationCanceledException`, so an HTTP client's timeout inside a work is
  invisible where the framework promises an event of its own.
- **Two works can be registered under one name.** The name keys the grain that runs the work, so the
  second work never runs and nothing says so.

## What Changes

- **Both backfills page by key.** The transaction-id backfill finds each batch's last entry first and
  stamps a range of the primary key; the partition counter's positions one batch per transaction
  instead of a partition per transaction. A long history is walked once.
- **The head says what it is.** A head is taken while nothing appends; taken while writers run it is
  held back, and a consumer seeded at it applies what those writers commit rather than skipping it.
  The contract, the spec and a test say so.
- **A work's failure is logged whatever it failed with**, except where its silo is stopping.
- **Two works under one name are refused at registration**, naming both.

## Impact

- Affected specs: `event-sourcing-store`, `orleans-execution`
- Affected code: `src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/CommitTransactionIdBackfill.cs`,
  `src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PartitionCounterBackfill.cs`,
  `src/Stratara.Abstractions/Abstractions/CommitOrder/ICommittedPositionReader.cs`,
  `src/Stratara.Orleans/Singleton/SingletonWorkGrain.cs`,
  `src/Stratara.Orleans/Singleton/SingletonWorkRegistrations.cs`
- Affected docs: `docs/guides/migrate-to-the-orleans-execution-model.md`, `CHANGELOG.md`
- No public API changes.
