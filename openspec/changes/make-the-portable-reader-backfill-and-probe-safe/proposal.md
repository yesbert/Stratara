# Make the portable reader's backfill and probe safe

> **Status:** proposed

## Why

The owner has deferred *extending* the portable commit-order reader: the framework's own products
run PostgreSQL with the native reader, and stability comes before provider breadth. This change does
not extend it. It exists because the `event-sourcing-store` specification today promises four things
about the portable reader that the code does not keep, and a promise the code does not keep is either
made true or withdrawn. The round-5 audit left them open as `R5-Rdr-002`, `R5-Rdr-003`, `R5-Rdr-009`
and `R5-Mig-011`.

- **The backfill renumbers what is already positioned and leaves every checkpoint standing.** It
  gives the unpositioned entries of a partition the positions from 1 and shifts every positioned entry
  up by their count. A reader with a checkpoint at 10 that stopped before an entry appended without
  the counter resumes after 10 once that entry is positioned — re-applies the entry that moved from 10
  to 11, and never reads the entry that was positioned at 1. The specification's scenario *A process
  appends without maintaining the counter* says the reader "resumes once the entry is positioned"; it
  resumes, and it loses the entry.
- **The probe for an unpositioned entry looks at sixty-four rows, unordered.** A reader checks before
  every read whether its partition holds an entry without a position, by fetching up to sixty-four
  unpositioned rows of any partition and filtering in memory. Sixty-five appends without the counter
  in other partitions hide the one in the reader's own, and the read passes it — the loss the reader
  exists to refuse.
- **Positions within one append follow the change tracker, not the stream.** The interceptor stamps
  the entries of one save in the order the tracker enumerates them; within one stream that is the
  version order today by accident, not by contract.
- **`CommitOrderOptions.MaintainPartitionCounter` is read by nothing in the framework.** It was
  documented in #106 as "the value a write context reads when it decides to" — a switch with no wire
  behind it, shipped with a default of `true` that switches nothing on.

## What Changes

- **The backfill positions unpositioned entries after the partition's counter and never touches a
  position already handed out.** Every checkpoint written before the backfill stays true; a reader
  resumes from it and reads the newly positioned entries once, after everything positioned before
  them. On a store adopted as documented — positioned before the first host with the counter starts
  — the order is the sequence order it was before. Where a host with the counter appended in between,
  the late entries are read after those appends, a later version of the same stream included; the
  documentation says so, says to stop the process that appends without the counter before
  positioning, and says that a read model that stalls on the resulting order is repaired by a rebuild.
- **The probe is per partition and complete:** a reader asks for the first unpositioned entry of its
  own partition, in sequence order, however many unpositioned entries other partitions hold.
- **Positions within one append follow the version order of each stream** and the order the streams
  were added, by contract and by test.
- **The switch is retired:** `CommitOrderOptions.MaintainPartitionCounter` is marked obsolete with a
  message naming the interceptor as what maintains the counter, and is removed with the next major. It
  is not wired, because wiring it would mean the framework adds an interceptor to a consumer's write
  context — the extension the owner has deferred.
- **Consumer-visible effects:** a backfill on a store with positioned entries now leaves them in
  place and appends after them, where it renumbered; a checkpoint survives a backfill, where the
  documentation said it must be reset; the reader stops at an unpositioned entry of its partition in
  every case; setting the obsolete option warns. No schema change. No change for a host with the
  native reader. Versioning: patch.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `event-sourcing-store`: *The store can be read in commit order without skipping a late committer* —
  positioning never changes a position handed out and a checkpoint survives it; the reader finds an
  unpositioned entry of its partition however many other partitions hold; positions within an append
  follow version order; the switch is obsolete; new scenarios.

## Impact

- `Stratara.Orleans` — `CommitOrderOptions.MaintainPartitionCounter` marked `[Obsolete]`.
- `Stratara.Orleans.EntityFrameworkCore` — `PartitionCounterBackfill` (append after the counter, no
  shift), `PortableCounterReader` (the per-partition probe), `PartitionCounterInterceptor` (stamp
  order).
- `docs/guides/migrate-to-the-orleans-execution-model.md` (the backfill's order, the window, the
  retired switch; the sentence "a checkpoint written before a backfill must be reset" goes),
  `docs/guides/operate-the-orleans-execution-model.md` (a partition that stopped at an unpositioned
  entry: what the positioned order is and when to rebuild), `src/Stratara.Orleans.EntityFrameworkCore/README.md`,
  `CHANGELOG.md`, `llms.txt`.
- Tests: `PartitionCounterBackfillTests` (its expectation flips from history-first to
  positioned-first), `PortableReaderFailsLoudlyTests` (a checkpointed reader across a backfill; the
  probe under many foreign unpositioned entries), an interceptor order test, and every test and test
  context that sets the obsolete option (`PocCommitOrderWriteDbContext` and the ~25 configurations in
  `tests/Stratara.Orleans.IntegrationTests`, `tests/Stratara.Orleans.Scenarios`,
  `tests/Stratara.Orleans.Benchmarks`) move to a switch of the test store's own.
- Versioning: patch.
