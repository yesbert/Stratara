## Context

See `proposal.md`, section Why. There are three readers of history that take the sequence number
for append order, and all three sit outside the two commit-order readers that
`read-what-was-written` fixed.

What shapes the approach:

- `ProjectionReplayWorker.ReplayBatchAsync` reads through
  `IEventStreamRepository.GetManyAfterSequenceAsync(after, batchSize)`, which orders by
  `SequenceNumber` and takes `batchSize` rows. The worker advances by the last entry's sequence
  number. It lives in `Stratara.Projections` and sees only the Tier-A repository interface. It has
  no commit record, because that column belongs to `Stratara.Orleans.EntityFrameworkCore` and
  exists only on a store that adopted the execution model.
- `CommitTransactionIdBackfill` stamps one PostgreSQL transaction id per batch. The batch runs from
  `from` to the sequence number of its `batchSize`-th unstamped entry. `PostgresTransactionIdReader`
  already orders the entries of one transaction by stream and version (`InStreamOrder`), so a stamped
  batch is read correctly inside itself. Only a stream whose entries straddle two batches comes out
  inverted.
- `PartitionCounterBackfill` hands out positions per partition in `SequenceNumber` order, 1,000
  entries per batch, through a set-based statement on PostgreSQL (`row_number() OVER (ORDER BY
  sequence)`) and a per-entry loop elsewhere. `PortableCounterReader` reads by position alone. Every
  inverted commit it positions is read inverted, whether or not a boundary splits it.
- Versions are consecutive per stream and entries are never removed, as the store's spec states. A
  stream lives in one bucket, and so in one partition.
- The live path is not involved. `EventSource` builds a bundle from its in-memory entries in append
  order, and the commit-order readers order a commit themselves.

## Goals / Non-Goals

**Goals:**

- Within a stream, the replay and both backfills produce version order. Across streams they keep
  the order the sequence gives.
- No existing entry is rewritten and no schema changes.
- Nothing changes for a store without inversions: the same batches and the same order as before.

**Non-Goals:**

- Repairing history that a backfill prepared before this release. Its commit records and positions
  are what every checkpoint on that store stands on, and the store's requirement forbids changing a
  position once handed out. Changing them is a different guarantee with its own sequence (stop
  appends, rewrite, rebuild), and no store is known to need it. The documentation gives the query
  that tells an operator whether theirs does. A store it finds is the evidence for a change of its
  own.
- Numbering entries in version order at write time. `read-what-was-written` rejected this, and the
  reason still holds (see the first decision below).
- The late committer during a replay: a transaction that allocated its sequence numbers before the
  replay passed them and commits afterwards. `docs/guides/migrate-to-the-orleans-execution-model.md`
  already names it, the second pass of a store-reading host covers it, and it does not concern the
  order within a stream.

## Decisions

### Order by reading, not by writing, as `read-what-was-written` decided

The decision and its rejected alternative are carried over from that change (archived
2026-09-22). EF Core decides the statement order of a batch itself, so sorting the tracked entries
before `SaveChanges` does not bind it. A fix on the write side would hold only by accident, and
only for the provider and batch size it was tested under. It would also leave every store written
so far inverted.

Evidence: the consumer's stream, three entries of one commit numbered 22, 21, 20 for versions 1, 2,
3, and 8,420 of 14,431 streams on one store carrying at least one inversion; the rationale in
`openspec/changes/archive/2026-09-22-read-what-was-written/design.md`.

### Within a range, give each stream's slots to its entries in version order

A batch covers a contiguous range of sequence numbers. Within the range, each stream holds a set of
sequence numbers, which are its *slots*. The entries of the stream are sorted by version and laid
into its slots in ascending order. The slots of different streams do not move, so the interleaving
between streams is the sequence's, and a range without inversions comes out unchanged.

*Alternative rejected: read whole commits.* This is what the fix proposal suggests and what the
native reader does. The replay has no commit record to read. The one that exists belongs to a Tier-C
package and to stores that adopted the execution model, and taking it would make the replay depend
on both.

*Alternative rejected: track each stream's last applied version and hold back an early arrival.*
The replay is correct without the bookkeeping, but the map grows with every stream in the store,
and the replay does not resume, so nothing would bound it.

*Alternative rejected: sort the batch by stream, then version.* It fixes the order within a stream
and throws away the order between streams, which a projection that joins two aggregates may rely
on.

### Extend a range until no stream in it continues below its top

The initial end is the `batchSize`-th entry after the start. One query then asks for the highest
sequence number beyond the end of any entry whose stream appears in the range at a higher version:
a join of the entries beyond the end with the range grouped by bucket and stream, taking each
stream's highest version. The join names the bucket as well as the stream, although the stream
determines it, because the store's unique index on the event table leads with the bucket. When it
answers, the end moves there and the query runs again, because the widened range can bring in a
stream of its own. When it answers nothing, the range is final.

This is enough across batches. Once the query answers nothing, every entry of a stream in the range
that lies beyond it has a higher version than the stream's highest in the range. By induction, a
stream's applied versions are always a prefix of its versions.

*Alternative rejected: pull only the stragglers into the batch.* The next batch starts after the
original end and would read them a second time. Avoiding that means keeping a set of what was
already pulled, which is the bookkeeping rejected above.

*Alternative rejected: a fixed look-ahead window.* Nothing bounds how far apart the sequence numbers
of one save can land while other writers allocate between them.

The query is expressed in LINQ, as a group-by and a join, so it translates on every provider the
store ships. There is no per-stream parameter list, so no provider's parameter limit comes into
play. Evidence to be produced: the SQLite and PostgreSQL tests of the new member (tasks 1.3 and 1.4).

### A new repository member with a default that keeps today's order

`IEventStreamRepository` gains `GetManyAfterSequenceInStreamOrderAsync(afterSequenceNumber,
batchSize, cancellationToken)`, which returns a range as above, ordered as above. The caller
advances by the highest sequence number in the result, not by the last entry's. The EF repository
implements it with the query above and an in-memory reordering of the loaded range.

The default interface implementation returns `GetManyAfterSequenceAsync`, sequence order, as today.
`IOutboxRepository.AddAsync` and `IProjectionCheckpointStore.FindAsync` are the precedent for
growing an Abstractions interface through a default member rather than a break.

*Alternative rejected: change `GetManyAfterSequenceAsync` in place.* Its only caller in the
framework is the replay, but it is public. A consumer that calls it gets a batch whose size and
order silently change.

*Alternative rejected: a default that throws `NotSupportedException`,* as the two precedents do. A
consumer with a repository of their own, whose store may well number in version order, would find
their replay broken by an upgrade that fixes replays. Keeping today's order changes nothing for them
and is stated in the member's documentation.

### The backfills apply the same range rule, and the portable one the same slot rule

`CommitTransactionIdBackfill`'s bound statement keeps extending the batch end with the same
straggler query, restricted to unstamped entries, until it answers nothing. The stamp statement is
unchanged: one transaction id per range, and the native reader orders inside it.

`PartitionCounterBackfill` extends its batch the same way, within the partition and among
unpositioned entries. It hands out the batch's positions by slot: the slot ordinal comes from
sequence order across the batch, and each stream's entries take its slots in version order. On
PostgreSQL this is two `row_number()` windows per stream (by sequence, by version) joined on their
rank. The per-entry path computes the same assignment in memory before it writes.

Both backfills remain idempotent on prepared history, because the unstamped and the unpositioned
predicates still select nothing.

Evidence to be produced: the backfill tests in `tests/Stratara.Orleans.IntegrationTests/CommitOrder`
on PostgreSQL, and a SQLite test for the per-entry positioning beside
`tests/Stratara.Testing.Orleans.Tests/SqlitePortableReaderTests.cs`.

## Risks / Trade-offs

- **The extension query costs a round trip per batch.** → One join per round, served by the
  unique bucket, stream and version index the store already declares. With a replay batch of
  5,000, one extra query per batch is small beside loading and applying the batch. Nearly every
  range needs one round, the one that answers nothing.
- **A batch can grow past the configured size.** → Only by the spread of the inverted saves it
  touches, which is the size of a save. The commit-order readers already return a transaction whole
  when it is larger than a batch. The replay requirement states it.
- **A consumer's own repository keeps the old order.** → That is the default member's documented
  behaviour. The CHANGELOG entry says that such a repository overrides the member to get the
  guarantee.
- **Moq-based tests set up the old member.** → The replay worker's tests move to the new member.
  Moq does not call an interface's default implementation unless told to, so a missed setup fails
  loudly instead of passing on the old path.
- **History prepared by an earlier release may be inverted.** → See Non-Goals. The documentation
  gives the detection query, and the CHANGELOG entry points to it.

## Migration Plan

Nothing to run. Upgrading is enough for the replay, and for any backfill run after the upgrade. A
store that was backfilled before the upgrade can be checked with the query in the Orleans migration
guide. An operator who finds a stream there has a store the next change is about.

## Open Questions

None that would change the specs or the tasks. Whether the extension query needs an index beyond
the bucket, stream and version one is answered by a replay against a large store. The tests hold
too few rows for a plan to say anything about that.
