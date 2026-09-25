## 1. The repository returns a range in stream order

- [x] 1.1 Add `GetManyAfterSequenceInStreamOrderAsync(long afterSequenceNumber, int batchSize,
  CancellationToken)` to `IEventStreamRepository`
  (`src/Stratara.Abstractions/Abstractions/EventSourcing/IEventStreamRepository.cs`). The default
  implementation returns `GetManyAfterSequenceAsync`. The XML documentation states the contract (a
  contiguous range, extended until no stream in it continues below its top, each stream's entries
  in version order over its own slots, advance by the highest sequence number) and that the default
  keeps sequence order.
- [x] 1.2 Implement it in `EventStreamRepository`
  (`src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/EventSourcing/EventStreamRepository.cs`):
  find the initial end, run the extension query (a join on bucket and stream against the range
  grouped by bucket and stream, taking the highest version) until it answers nothing, load the
  range, and lay each stream's entries into its slots in version order.
- [x] 1.3 In `tests/Stratara.WriteStore.Tests`, write a new test class next to
  `EventStreamRepositorySequentialTests`. It inserts rows with explicit sequence numbers so that one
  stream's versions 1 to 3 sit at 22, 21, 20, and covers:
  - the order within one range;
  - a batch size that ends the range at 21, where the range is extended and nothing is returned
    twice across two calls;
  - two inverted streams interleaved, where the order between them follows the sequence;
  - a store with no inversion, which returns exactly what `GetManyAfterSequenceAsync` returns.
- [x] 1.4 In `tests/Stratara.Orleans.IntegrationTests`, the suite that holds a PostgreSQL write
  store, add the same inverted-commit and boundary cases against PostgreSQL
  (`CommitOrder/ReplayStreamOrderTests.cs`). The extension query's plan is not recorded: on the
  test's handful of rows the planner's choice says nothing about a large store.

## 2. The replay reads through it

- [x] 2.1 In `ProjectionReplayWorker.ReplayBatchAsync`
  (`src/Stratara.Projections/Services/ProjectionReplayWorker.cs`), read through the new member and
  return the batch's highest sequence number, not `entries[^1].SequenceNumber`. Update the class
  summary, which says the replay runs in sequence-number order.
- [x] 2.2 Move the setups in `tests/Stratara.Projections.Tests/Services/ProjectionReplayWorkerTests.cs`
  from `GetManyAfterSequenceAsync` to the new member.
- [x] 2.3 Add a test in that file: a batch whose entries come back reordered, with the highest
  sequence number not last. Assert that the next batch starts after the highest sequence number and
  that the projection manager receives the entries in the order returned.

## 3. The native backfill never splits a stream's inverted run

- [x] 3.1 In `CommitTransactionIdBackfill`
  (`src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/CommitTransactionIdBackfill.cs`), extend
  the bound: after `BoundAsync`, repeat a straggler statement over unstamped entries beyond the bound
  until it returns nothing. Update the class summary, which equates sequence order with append
  order.
- [x] 3.2 Add a test in `tests/Stratara.Orleans.IntegrationTests/CommitOrder`, beside
  `TransactionIdMigrationTests`. It uses a stream whose versions are numbered in reverse and a
  backfill batch size that ends a batch between them, then asserts that the native reader returns
  them in version order.

## 4. The portable backfill positions by version within each stream

- [x] 4.1 In `PartitionCounterBackfill`
  (`src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PartitionCounterBackfill.cs`), extend the
  batch the same way, among the partition's unpositioned entries.
- [x] 4.2 In the set-based statement, assign the positions by slot with a `row_number()` by sequence
  and one by version per stream, joined on rank.
- [x] 4.3 In `PositionEachAsync`, compute the same assignment in memory before writing. Update the
  class summary.
- [x] 4.4 Extend `tests/Stratara.Orleans.IntegrationTests/CommitOrder/PartitionCounterBackfillTests.cs`
  with an inverted stream, with and without a batch boundary between its versions. Assert both the
  positions and what the portable reader returns.
- [x] 4.5 Add a SQLite test of the per-entry positioning beside
  `tests/Stratara.Testing.Orleans.Tests/SqlitePortableReaderTests.cs`.

## 5. Documentation

- [x] 5.1 In `docs/guides/write-a-projection.md`, under *Replay is destructive, and it is
  all-or-nothing*, state that a replay applies each stream in version order and that a batch can be
  larger than `Projections:BatchSize`.
- [x] 5.2 In `docs/guides/migrate-to-the-orleans-execution-model.md`, update both backfill sections:
  - say that a save does not number its entries in version order and that the backfill orders each
    stream by version regardless;
  - give the query that lists streams whose commit records or positions, prepared by an earlier
    release, contradict their versions.
  
  Also correct the full-replay section's "in sequence order".
- [x] 5.3 Add the entry to `CHANGELOG.md` under Unreleased. Name the new repository member and its
  default, that history backfilled earlier is not rewritten, and where the detection query is.

## 6. Gate

- [x] 6.1 `openspec validate replay-each-stream-in-the-order-it-was-written --strict`
- [x] 6.2 `./scripts/local-gauntlet.sh`
- [x] 6.3 `dotnet test tests/Stratara.Orleans.IntegrationTests` (Docker; the gauntlet skips it)
