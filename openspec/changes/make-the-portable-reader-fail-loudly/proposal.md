# Make the portable reader fail loudly

> **Status:** approved (owner, 2026-09-16)

## Why

The Orleans execution model reads the event store in commit order through one of two readers. The
native PostgreSQL reader held up in the readiness review of 2026-09-16; the portable reader, which
orders by a per-partition counter and is meant for any relational provider, can lose committed facts
**without any error**:

- An entry appended by a process whose write context does not maintain the counter has no position,
  and the reader never sees it. Maintaining the counter is a manual step on every write context, the
  option that looks like its switch is read nowhere in the framework, and the check for unpositioned
  entries runs once, at the start of a reading host.
- Lowering the partition count merges partitions whose positions overlap. The reader refuses the old
  checkpoints and asks for a reset, but after the reset new appends are still skipped until the counter
  passes the merged maximum.
- When positioning an append fails, the interceptor leaves the transaction it opened behind, and a save
  retried on the same context fails with an unrelated exception.

The owner decided (2026-09-16) to make these failures loud now and to defer finishing the portable reader
— automatic registration, renumbering, tests on other providers — until a user needs a provider other
than PostgreSQL. Every product built on Stratara today runs on PostgreSQL.

## What Changes

- **A reader stops at an unpositioned entry.** While it reads, the portable reader detects an entry in its
  partition that has no position and fails the read with a message naming the missing interceptor and the
  backfill. The partition stops, as it does for any failing entry: the failure is logged, the stall is
  counted, and reading resumes once the entry is positioned.
- **A lowered partition count refuses to start.** A host that reads with the portable reader refuses to
  start when the store holds counter rows for partitions at or above its partition count, naming both.
- **The interceptor cleans up after itself.** A failure while positioning an append, or while committing,
  rolls back and releases the transaction the interceptor opened, so a retried save on the same context
  behaves like a first one.
- **The documentation says what the portable reader is.** Verified on PostgreSQL only; every process that
  appends needs the interceptor; the partition count does not change without renumbering, which the
  framework does not offer. `MaintainPartitionCounter` is documented as what it is: a value a write
  context reads when it adds the interceptor, not a switch the framework consults.
- No new public type or member; no schema change. A store whose appends are all positioned and whose
  partition count never changed notices nothing but one extra existence query per read.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `event-sourcing-store`: *The store can be read in commit order without skipping a late committer* — the
  portable reader's preconditions (every appending process maintains the counter; the partition count is
  fixed) and its behaviour when they are broken; two scenarios, verified on PostgreSQL only.

## Impact

- `Stratara.Orleans.EntityFrameworkCore` — `PortableCounterReader`, `PortableCounterStartupCheck`,
  `PartitionCounterInterceptor`, the XML documentation of `AddStrataraPortableCounterReader`.
- `Stratara.Orleans` — the XML documentation of `CommitOrderOptions.MaintainPartitionCounter` and
  `PartitionCount`.
- `docs/guides/migrate-to-the-orleans-execution-model.md` (*Choose a commit-order reader*),
  `docs/guides/operate-the-orleans-execution-model.md` (*A partition that stops advancing*),
  `llms.txt` (the package line naming the portable counter).
- Tests: integration tests on PostgreSQL for an unpositioned append and a lowered partition count; a unit
  or integration test for the interceptor's failure paths.
- Versioning: patch.
- Deferred, owner decision 2026-09-16: registering the interceptor automatically, renumbering after a
  partition-count change, tests on SQLite and SQL Server.
