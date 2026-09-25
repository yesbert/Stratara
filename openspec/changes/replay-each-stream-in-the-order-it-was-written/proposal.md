# Replay each stream in the order it was written

> **Status:** approved

## Why

A consumer on 4.3.0 started its first projection replay and it failed on the first batch, five
attempts, with a projection reporting that a stream's third fact referred to an entity not applied
yet. The stream was sound: versions 1 to 3, one commit, rising timestamps. Their sequence numbers
ran the other way, 22, 21, 20, and the replay walks the store in sequence order. Because a replay
empties every read model before it rebuilds, each attempt left the read side empty. On that store
more than half of 14,431 streams carry such an inversion, so no replay can complete there at all.

The inversion is not new and not the consumer's doing. It is the one `read-what-was-written` found
on the live path: EF Core chooses the statement order of a save, the identity column numbers rows
as they arrive, and the entries of one save get sequence numbers that can contradict their
versions. That change ordered the entries of one commit in the two commit-order readers. It did not
touch the replay, and it did not touch the two backfills that position existing history for those
readers. Both still take the sequence number for append order, which the store's own requirement
says they must not.

## What Changes

- A projection replay applies each stream's entries in version order, however the store's sequence
  numbered them. Entries of different streams keep the order the sequence gives them.
- A replay batch never ends where a stream it holds still has a lower version beyond its end. Such a
  batch is extended until none is left, so a batch may be larger than the configured size, as a
  commit-order batch already may.
- The backfill that stamps the PostgreSQL commit record on existing history never ends a batch where
  a stream it holds continues below its highest version. The native reader already orders each
  stamped batch by version, so this is enough for backfilled history to be read in version order.
- The backfill that positions existing history for the portable reader hands out a partition's
  positions so that each stream's positions follow its versions, and never ends a batch where a
  stream it holds continues below its highest version.
- No existing entry is rewritten and no data migration is needed. A store that already holds
  inverted sequence numbers replays correctly after the upgrade. A store that was backfilled before
  the upgrade keeps the positions and commit records it was given. The documentation says how to
  find out whether that history is affected and how to repair it.
- `IEventStreamRepository` gains one member that returns a replay batch in stream order. It has a
  default implementation, so a repository written against 4.3.0 still compiles and keeps today's
  sequence-order behaviour until it overrides the member.

The live path does not change: a bundle published after a save carries its entries in append order,
and the commit-order readers already order a commit.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `projections`: the replay gains a requirement that it applies each stream in version order and
  that no batch ends inside a stream's out-of-order run.
- `event-sourcing-store`: the two backfills gain a requirement that history they prepare is read in
  version order within each stream, even where the store's sequence numbers say otherwise.

## Impact

- `src/Stratara.Abstractions/Abstractions/EventSourcing/IEventStreamRepository.cs`: the new member
  and its default implementation.
- `src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/EventSourcing/EventStreamRepository.cs`:
  the set-based implementation.
- `src/Stratara.EventSourcing.EntityFrameworkCore/Stratara.EventSourcing.EntityFrameworkCore.csproj`:
  internals visible to `Stratara.Orleans.IntegrationTests`, which reads the range on PostgreSQL.
- `src/Stratara.Projections/Services/ProjectionReplayWorker.cs`: reads through the new member and
  takes a batch's end from the range it covers, not from its last entry.
- `src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/CommitTransactionIdBackfill.cs`: the batch
  bound.
- `src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PartitionCounterBackfill.cs`: the batch
  bound and the positioning order, on both the set-based and the per-entry path.
- Tests in `tests/Stratara.WriteStore.Tests`, `tests/Stratara.Projections.Tests` and
  `tests/Stratara.Orleans.IntegrationTests`, each with a commit whose sequence numbers run against
  its versions and a batch boundary that splits it.
- `docs/guides/write-a-projection.md` (what a replay promises about order) and
  `docs/guides/migrate-to-the-orleans-execution-model.md` (what the backfills promise, and how to
  repair history backfilled before this release).
- `CHANGELOG.md`.
- Nothing is dissolved or superseded by this change.
