## Context

Three readers end a batch the same way: the event stream repository's stream-order read (the replay),
`PartitionCounterBackfill` and `CommitTransactionIdBackfill`. Each takes a range of sequence numbers `(from, to]`,
finds the highest version `top` of each stream in it, and looks beyond `to` for an entry of that stream with a lower
version (a *straggler*). While one exists, the range is extended to it. The search is a join of the batch's
`(bucket, stream, top)` rows against the event table on `(bucket, stream)` with `version < top` and `seq > to`. On
the unique `(bucket, stream, version)` index that is a range scan over every version of the stream below `top`, which
means the stream's whole past. The planner's other choice is every entry beyond `to`, which means the store's whole
future. Either way, a stream present in every batch is re-read in full by every batch.

`PartitionCounterBackfill` reads through `context.Set<EventStreamEntry>()`, so it sees the context's global query
filters. `EventStreamEntry` is `IMultiTenant`, and `ApplyGlobalTenantQueryFilters` puts a tenant filter on it. With no
session the filter matches nothing, so `BoundAsync` returns null and the partition is reported as done. The PostgreSQL
positioning statement is raw SQL and unfiltered. The per-entry path's `ExecuteUpdateAsync` is filtered.
`CommitTransactionIdBackfill` is raw SQL throughout and is not affected.

## Goals / Non-Goals

**Goals:**

- Per batch, work proportional to the batch plus the entries it is extended by, whatever a stream holds outside it.
- Identical results: the same ranges, the same order, the same positions and stamps.
- The portable backfill sees every entry, on every provider path.

**Non-Goals:**

- Changing where a batch ends, the extension rule, or the public signatures.
- Rewriting or revisiting history prepared by an earlier release (the spec forbids it).
- Query filters anywhere but the portable backfill. The review found one operation that was inconsistent with
  itself: the portable backfill read through the filters but positioned without them. The framework's other
  store-wide work reads through the write context's filters as well: both commit-order readers and the portable
  reader's startup check, the replay's read, and the hash chain. Whether a write context that filters the
  framework's own entries by tenant is supported at all is a decision for the owner. It is raised separately and
  not settled here.

## Decisions

### Bound each stream's search from below by what was consumed before the range

For each stream in the range, take `floor`: the highest version below `top` whose entry lies at or before `from`. The
search is then limited to versions strictly between `floor` and `top`. `floor` is one backward step on the
`(bucket, stream, version)` index from `top`, skipping only the stream's entries inside the range and its stragglers.
The straggler search becomes a range scan over `(floor, top)`, which holds only the range's own entries of that stream
and its stragglers.

**Why the results are identical.** Call a stream's entries at or before `from` its consumed versions. Claim: whenever
`from` is `0` or the end of a previous range of the same walk, every stream's consumed versions are a prefix
`1..P`. It holds at `0`. Inductively, a range ends only once no stream in it has an unconsumed version below its
`top` beyond the end. So afterwards each such stream has consumed `1..top`. A higher version cannot already be
consumed: the range's own entries of that stream would then be below `P` and consumed already. Streams outside the
range are unchanged. Given the prefix, every version at or below `floor` is consumed and so lies at or before `from`.
The versions the old search could find, below `top` and beyond `to`, are therefore exactly those in `(floor, top)`.
The claim does not depend on how saves number their entries. The test fixtures that append a stream's versions in
reverse order through separate saves satisfy it just as well.

*For the backfills* the same walk applies. Each run starts at `0` and continues from the end of its previous batch.
"Consumed" becomes "at or before `from`", and the search stays restricted to unprepared entries. An entry prepared
before the run, at or before `from`, that sits above an unprepared lower version beyond `to` would break the prefix.
That needs a later save of a stream to carry a lower version than an earlier one, which optimistic concurrency on the
unique version index rules out in a real store.

*Evidence:* the existing inversion tests (`ReplayStreamOrderTests`, `EventStreamRepositoryStreamOrderTests`,
`PartitionCounterBackfillTests`, `TransactionIdMigrationTests`) pass unchanged. So does a new PostgreSQL test over a
long stream whose commits are numbered against their versions. That test checks every stream's order and counts
the rows the walk touches.

**Alternatives considered.**

- *Bound from below by the lowest version in the range.* Wrong: a straggler can sit below it (versions 5, 6, 7
  numbered 103, 101, 102 with the range ending at 102 put 6 and 7 in the range and leave 5 beyond it).
- *Record a save identity on each entry and extend by save.* A schema change on the busiest table, and it does not
  help history that was written without it.
- *Compare counts: a stream is complete when the range holds `top - P` of its entries.* This needs `P`, which is the
  same backward step, and the straggler's sequence number is still needed to extend the range.

### Keep one query per batch, portable where it was portable, with the floor in a derived table

The replay's read and the portable backfill stay LINQ, so SQLite and SQL Server keep working. `floor` is a
correlated `OrderByDescending(version).Select(version).FirstOrDefault()` per grouped stream. The native backfill stays
raw SQL with the same shape.

Left to itself, EF inlines the floor subquery into the join's `WHERE`. PostgreSQL then evaluates it for every candidate
entry of the stream and cannot use it to bound the index scan. The counted-rows test measured the replay's read at 28.7
rows per entry that way. The grouped tops and the tops with their floors are therefore each followed by `Distinct()`.
It changes nothing, since `(bucket, stream)` is already unique, but it makes each stage a derived table of its own.
The plan on two million entries shows the intended shape: one backward index step per stream for the floor, and an
index scan bounded by `version > floor AND version < top`. A single `Distinct()` after the floor is not enough on
SQLite, which then rejects the floor's reference to `max(version)` ("misuse of aggregate function").

*Evidence:* the counted-rows test, and `EXPLAIN ANALYZE` on the benchmark store (23 ms per straggler query at batch
5000 with 1,126 streams).

### Where a backfill batch ends is a window of the key, not of the unprepared entries

Both backfills used to end a batch at the batch size's worth of *unprepared* entries after `from`. For the portable
backfill that also meant *of its partition*: `ORDER BY seq LIMIT n` under `position IS NULL` and `bucket % P = p`.
PostgreSQL estimates `bucket % P = p` at the default 0.5% and `IS NULL` from whatever statistics it has. It then
concludes that the `LIMIT` will not cut the scan short, and chooses a bitmap scan over everything after `from`, for
every batch. On two million entries that took 470 ms per batch for the portable backfill. For the native one, the plan
flipped mid-run to about 155 ms per batch in 499 of 2,000 batches once autovacuum's statistics changed. Both are
quadratic, independent of any long stream.

The batch now ends at the last of the next `n` entries after `from` in key order, whatever their state: `n` is the
batch size for the native backfill, and 1,000 × the partition count for the portable one, which is a partition's
1,000 when the partitions share the store evenly. Finding that is a range of the primary key. The batch then prepares
the unprepared entries of the window (of its partition) and is extended by stragglers as before. Positions and stamps
do not change: batch boundaries only group, and the extension keeps every stream's inverted run inside one batch.

*Consequences:* a portable batch holds about 1,000 entries of its partition rather than exactly 1,000. A window can
hold none of a partition's entries and positions nothing, and each partition still walks the whole key range, as it
did before whenever the plan was good. A second run over a prepared store walks it once instead of stopping at the
first query. The native backfill's `batchSize` becomes the number of consecutive entries a batch covers. During the
documented migration, where nothing is stamped yet, that is the number it stamps.

*Evidence:* the counted-rows tests (portable 30.4 → 5.7 rows per entry, native 253.6 → 4.7). The benchmark: native
backfill 78.5 s → 19.8 s, portable 51.9 s → 49.2 s.

### Ignore query filters in the portable backfill

Every read and update of `EventStreamEntry` in `PartitionCounterBackfill` goes through `IgnoreQueryFilters()`. That
covers the bound, the straggler search with its floor, the per-entry slots and the per-entry update. The counter table
is not filtered, and the PostgreSQL statement is raw SQL already. *Evidence:* a test with a write context that applies
the framework's tenant query filters. It fails today, positioning nothing. It passes after the change, on both
positioning paths.

### Document the condition instead of enforcing it

`GetManyAfterSequenceInStreamOrderAsync` states in its remarks that the version order is guaranteed from `0` and
from the highest sequence number of a result it returned. From any other position a stream can be read out of order.
The replay worker already reads this way, and the contract has always depended on it. Enforcing it would take state
the repository does not have.

## Risks / Trade-offs

- [The planner chooses a scan for the floor or the search on some table size] → The counted-rows test runs at a size
  where the old search visibly re-reads the long stream. It bounds rows touched per entry, so a plan regression
  fails it.
- [A consumer calls the stream-order read from an arbitrary position] → That was never ordered, and the result is
  still every entry of a contiguous range. The remarks now say so.
- [Many short streams in one batch: one extra index probe per stream] → The probe is `O(log n)`, about 12 µs on the
  benchmark store, and it replaces a range scan that cost at least as much. The benchmark in tasks.md records before
  and after.
- [The tests that forced a boundary at 1,000 entries lose it] → The portable inverted-run tests (PostgreSQL and
  SQLite) now use `1000 × partition count − 5` fillers. The counter-check, with the extension switched off, fails both.
