## Context

The motivation and the owner's scoping are in `proposal.md`. The code today:

- `PortableCounterReader.ReadAfterAsync` (`src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PortableCounterReader.cs`)
  selects entries of its partition with `partition_position > after`; an entry whose position is null is
  never selected.
- `PortableCounterStartupCheck` refuses a host while any entry lacks a position, once, at start.
- The partition count lives only in `CommitOrderOptions.PartitionCount`. The store holds one
  `partition_position` row per partition, seeded by the host or created by `PartitionCounterBackfill`
  (`LockCounterAsync`). A missing row makes the interceptor throw; a surplus row is never looked at.
- `event_stream_entry.partition_position` is indexed (`EventStreamEntryConfiguration.cs:30`); B-tree indexes
  on PostgreSQL and SQL Server include nulls, so `IS NULL` is an index lookup.
- `PartitionCounterInterceptor` opens a transaction in `SavingChangesAsync` when none is current and keeps
  it in a `ConditionalWeakTable`; it commits in `SavedChangesAsync` and rolls back in
  `SaveChangesFailedAsync`. EF Core does not call `SaveChangesFailedAsync` when `SavingChangesAsync`
  itself throws, and `SavedChangesAsync` removes the entry before committing.
- A read that throws is logged by the store-reader grain (`117_103`), the partition does not advance, and
  the next wake-up or poll reads again (`StoreReaderGrain.cs:75-82`).
- `CommitOrderOptions.MaintainPartitionCounter` is read only by test and benchmark write contexts that
  decide whether to add the interceptor.

## Goals / Non-Goals

**Goals:**
- No silent loss under the portable reader for the two ways it can happen: an append without a position,
  a lowered partition count.
- The interceptor leaves nothing behind on any failure path.
- Documentation and XML documentation state the preconditions and that the reader is verified on
  PostgreSQL only.

**Non-Goals** (deferred, owner decision 2026-09-16):
- Registering the interceptor from `AddStrataraPortableCounterReader` or any write-context registration.
- Renumbering a store after a partition-count change.
- Tests on SQLite or SQL Server.

## Decisions

### D1 — The reader looks for unpositioned entries through the position index

Before selecting a batch, `ReadAfterAsync` asks for a small number of entries whose position is null —
`WHERE partition_position IS NULL`, bounded, projecting the bucket and sequence number — and maps their
buckets to partitions in memory with the same partition function the reader uses. If one belongs to the
reader's partition, the read throws `InvalidOperationException` naming the entry's sequence number,
`PartitionCounterInterceptor` and `PartitionCounterBackfill`. The grain logs it and the partition stops,
which is exactly the behaviour of a failing entry. Positioning the entry — the backfill — lets the next
read continue.

*Rejected: filtering by partition in SQL* — `bucket_id % n = p AND partition_position IS NULL` cannot use
the index and scans the table on every read.
*Rejected: checking only at start* — today's check; the defect is an append after start.
*Rejected: skipping and logging* — reading past the entry is the loss this change exists to prevent.

Evidence: `PortableCounterReader.cs`, `EventStreamEntryConfiguration.cs:30`, `StoreReaderGrain.cs:75-82`.

### D2 — The start check refuses surplus counter rows

`PortableCounterStartupCheck` additionally refuses to start when a `partition_position` row exists for a
partition at or above `PartitionCount`, naming the configured count and the highest counter row. Missing
rows are left to the interceptor, which already fails the append naming the partition.

*Rejected: storing the count and comparing* — a schema change for a fact the counter rows already carry.

Evidence: `PortableCounterStartupCheck.cs`; `PartitionCounterInterceptor.cs` (missing-row message).

### D3 — The interceptor releases its transaction on every path

`SavingChangesAsync` wraps the positioning in a try/catch that, when it opened the transaction, rolls
it back, disposes it and removes it before rethrowing. `SavedChangesAsync` commits before removing, and
rolls back and disposes when the commit throws. Registering the transaction replaces a stale entry
instead of throwing on an existing key.

Evidence: `PartitionCounterInterceptor.cs:53-88`; the review's probe against EF Core 10.0.11 (not
committed).

*Found during apply (2026-09-16):* the review overstated the defect. A save retried on the same context after
a failed positioning does not fail under the 4.1.1 interceptor: the transaction left behind is still the
context's current one, so the retry opens none, and `SavedChangesAsync` finds the left-over entry and commits
it. `PortableReaderFailsLoudlyTests.A_save_whose_positioning_failed_leaves_no_transaction_for_the_next_save_on_the_context`
passes against both. What remains is a transaction that stays open on a context until the context is disposed
or saves again — anything else run on that context in between runs inside it. D3 is kept as hardening with that
test as its regression guard, and is not described as a consumer-visible fix.

### D4 — `MaintainPartitionCounter` stays, documented as what it is

Its XML documentation says that the framework does not read it: a write context reads it when it decides
whether to add `PartitionCounterInterceptor`, and every process that appends to a store read by the
portable reader must add it.

*Rejected: `[Obsolete]`* — a consumer building with warnings as errors breaks on a patch.
*Rejected: having the framework honour it* — that is automatic registration, deferred.

## Risks / Trade-offs

- [One extra indexed query per read] → bounded and index-backed; measured in the existing commit-order
  benchmarks if they run in the apply, otherwise stated as unmeasured.
- [An unpositioned entry in another partition is found on every read of every partition] → the bounded
  query returns it each time and the partition function discards it; a store with many unpositioned
  entries in other partitions is already refused at start and after the fix stops those partitions.
- [A provider whose index excludes nulls] → the query is correct, only slower; the reader is documented
  as verified on PostgreSQL only.

## Migration Plan

Patch release. No schema change. A deployment with an unpositioned entry that went unnoticed now sees its
partition stop with a message naming the backfill; running `PartitionCounterBackfill.RunAsync` resolves it.
