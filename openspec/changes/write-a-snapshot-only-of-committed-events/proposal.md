# Write a snapshot only of committed events

> **Status:** approved

## Why

A snapshot is written in a transaction of its own, before the events it captures are committed. When
the events' commit then fails, the snapshot stays: a save that loses a concurrency race leaves a
snapshot of events that were never recorded. The next rebuild starts from that snapshot, so the
aggregate carries state from a write that did not happen. Nobody notices, because the rebuild
succeeds. The same ordering makes a clash on the snapshot's own unique version surface as the
provider's raw exception rather than as a concurrency conflict.

## What Changes

The consumer-visible effect: a snapshot captures only committed events. A save that fails leaves no
snapshot behind, and a snapshot that cannot be written does not fail a save whose events are
committed.

- The event source writes the snapshot after the events are committed and their bundle handed on,
  from the committed stream up to the batch's highest version, instead of before the commit from the
  batch.
- A failure to write the snapshot after the commit is logged
  (`LogEvents.EventStore.SnapshotFailed`, `102_006`) and does not fail the save. A snapshot is a
  cache; the next threshold writes one.
- A save that fails, a concurrency conflict included, writes no snapshot.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `aggregate-rehydration`: *Snapshots shorten a replay without changing its result* says a snapshot
  captures only committed events, and covers a save that fails and a snapshot that cannot be written.

## Impact

- `src/Stratara.Infrastructure/EventSourcing/EventSource.cs` — the snapshot runs after the commit,
  and its failure is logged.
- `src/Stratara.Infrastructure/EventSourcing/SnapshotService.cs` — builds the snapshot from the
  committed stream up to the batch's highest version.
- `src/Stratara.Abstractions/Abstractions/EventSourcing/ISnapshotService.cs` — documents that it runs
  after the commit.
- `src/Stratara.Diagnostics/LogEvents.cs` — `EventStore.SnapshotFailed`.
- `tests/Stratara.Infrastructure.Tests/EventSourcing/` — a lost race leaves no snapshot; a failing
  snapshot does not fail the save.
- `CHANGELOG.md`.
