# Read and prepare a long history in linear time

> **Status:** approved

## Why

A store with one long-lived stream is replayed, and prepared for either commit-order reader, in time that
grows with the square of its history. Before a batch of history can end, the framework looks for entries of a
lower version, beyond the batch, of every stream the batch holds. That search reads everything the stream
holds below the batch's highest version, so every batch re-reads the long stream's past. The independent
review of 4.3.1 measured it on two million entries, one stream holding a tenth of them: 15.5 s of that search
against 0.3 s of reading for a replay, and 94 s for a backfill. With millions of events in one stream, a single
search risks the database driver's default command timeout, which fails a replay batch and a backfill outright.

The same review found that the portable reader's backfill reads the store through the write context's query
filters but positions entries without them. A write context that filters entries by tenant, as the framework's
own helper for tenant query filters does, therefore hides the whole history from the backfill, and the backfill
positions nothing. The portable reader then refuses to start, or stops at the first entry it cannot read past.

The owner ruled on 2026-09-26 that 4.4.0 is not released while a known bug is open. Both defects are known.

## What Changes

- Reading history in stream order (the replay's read) and both backfills bound each stream's search to the
  versions that can still lie beyond the batch. The work a batch does no longer grows with how much history a
  stream holds outside it. What they return and what they prepare do not change.
- The portable reader's backfill positions every entry of the store, whatever query filters the consumer's write
  context declares.
- The replay's read states when its order holds: from the start of the store, or after the highest sequence
  number of a result it returned. That was always the condition, and the replay worker already reads that way;
  the bounded search relies on it too.
- The native backfill's batch size is documented as what it is: the least a batch stamps. A batch that would
  end inside a stream's inverted run grows until it does not.
- A replay over a real store whose sequence numbers run against its versions is tested end to end, from the
  replay worker down to the store, on SQLite and on PostgreSQL. So far only the store's read was tested there,
  and the worker only against a substitute.

No public signature changes. The only consumer-visible effect is the time these operations take. There is also
the backfill on a tenant-filtered write context, which now positions the history instead of nothing.

## Capabilities

### New Capabilities

### Modified Capabilities

- `projections`: *A replay applies each stream in the order it was written*: a replay's batch does not do work
  that grows with the history a stream holds outside the batch.
- `event-sourcing-store`: *Backfilled history is read in version order within each stream*: the same bound
  for both backfills, and the portable backfill positions every entry whatever query filters the write context
  declares.

## Impact

- `Stratara.EventSourcing.EntityFrameworkCore`: the event stream repository's stream-order read.
- `Stratara.Orleans.EntityFrameworkCore`: `PartitionCounterBackfill` and `CommitTransactionIdBackfill`.
- `Stratara.Abstractions`: remarks on `IEventStreamRepository.GetManyAfterSequenceInStreamOrderAsync`.
- Tests: `Stratara.WriteStore.Tests`, `Stratara.Projections.Tests`, `Stratara.Orleans.IntegrationTests`.
- `CHANGELOG.md` under the open `[4.4.0]`.
- This closes the 4.3.1 review follow-ups listed as release blockers in the project's state file, except the
  catalog item, which is a change of its own.
