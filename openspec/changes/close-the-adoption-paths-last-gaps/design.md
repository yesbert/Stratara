# Design

## D1 — Paging a backfill by key

**Decision.** Each batch reads its own upper bound first — the highest sequence number of the next
*n* entries without a commit record — and the update is then a range of the primary key. The partition
counter's backfill takes one batch per transaction instead of one partition.

**Why not an index on "without a commit record".** A partial index would have to be created before the
backfill and dropped after it, in a migration the consumer edits by hand; the key the rows are already
ordered by answers the same question for nothing.

**What the batching costs.** The counter's backfill now commits per batch, so an append that arrives
between two batches takes a position before entries the next batch positions. That is the same
condition the migration already states — run it while nothing appends — and it is now the only thing
that condition protects.

## D2 — The head is as honest as the store lets it be

**Decision.** State the precondition rather than change the head.

**Why not wait for the snapshot to pass.** A head could wait until every transaction open at the call
has ended, which would make it exact — and would make a seeding wait for a writer that never ends. The
deployment's own migration already requires a window with no appends, which is where a head is taken;
what was missing is that nobody said so.

## D3 — What this change does not do

`AddStrataraSingletonWork<TWork>(configure)` called twice adds its configure callback twice, and the
idempotency test deliberately leaves callbacks out of the composition it compares. A callback that sets
values is idempotent, which is what configuring an option means everywhere in this framework; a
callback that appends is not, and would be doubled. Changing that means deciding what two different
callbacks for one work mean, which is a change with a question in it rather than a fix.
