## 1. Bound the straggler search

- [x] 1.1 The event stream repository's stream-order read bounds each stream's search from below by the highest
  version under the stream's top that lies at or before the range's start
  (`src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/EventSourcing/EventStreamRepository.cs`,
  `FindStragglerAsync`); `EventStreamRepositoryStreamOrderTests` (SQLite) and `ReplayStreamOrderTests`
  (PostgreSQL) pass unchanged
- [x] 1.2 `PartitionCounterBackfill.FindStragglerAsync` takes the same bound over unpositioned entries
  (`src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PartitionCounterBackfill.cs`); `PartitionCounterBackfillTests`
  pass unchanged
- [x] 1.3 `CommitTransactionIdBackfill`'s straggler statement takes the same bound over unstamped entries
  (`src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/CommitTransactionIdBackfill.cs`); `TransactionIdMigrationTests`
  pass unchanged
- [x] 1.4 A PostgreSQL test over a long stream whose commits are partly numbered against their versions counts the
  rows the replay's read and each backfill touch, and asserts every stream's order and a bound linear in the store's
  entries (`tests/Stratara.Orleans.IntegrationTests/CommitOrder/LongStreamWorkTests.cs`); the counter-check —
  the old search restored — fails it
- [x] 1.5 Benchmark before and after on two million entries with one stream holding a tenth, recorded in this file
  (scratch harness, not committed). Replay at batch 5000: 13.1 s → 9.7 s. Portable backfill: 51.9 s → 49.2 s.
  Native backfill at batch 1000: 78.5 s → 19.8 s. Counted-rows tests on 100,000 entries, half of them one stream
  (rows read per entry, old → new): replay 206.9 → 9.4, portable 53.3 → 5.7, native 253.6 → 4.7
- [x] 1.6 Where both backfills end a batch is a window of the key, not a count of unprepared entries (design:
  *Where a backfill batch ends*); the inverted-run tests' fillers follow the window (`1000 × partition count − 5`)
  in `PartitionCounterBackfillTests` and `SqlitePartitionCounterBackfillTests`, and the counter-check (extension
  switched off) fails both

## 2. Query filters in the portable backfill

- [x] 2.1 Every read and update of the event table in `PartitionCounterBackfill` ignores query filters
- [x] 2.2 A test with a write context that applies the framework's tenant query filters positions every tenant's
  entries, in the order a context without the filter positions them — on PostgreSQL
  (`tests/Stratara.Orleans.IntegrationTests/CommitOrder/PartitionCounterBackfillTests.cs`) and on SQLite for the
  per-entry positioning (`tests/Stratara.Testing.Orleans.Tests/SqlitePartitionCounterBackfillTests.cs`); the
  counter-check (filters not ignored) fails both

## 3. Documentation

- [x] 3.1 `IEventStreamRepository.GetManyAfterSequenceInStreamOrderAsync` remarks: the version order holds from `0`
  and from the highest sequence number of a returned result
  (`src/Stratara.Abstractions/Abstractions/EventSourcing/IEventStreamRepository.cs`)
- [x] 3.2 `CommitTransactionIdBackfill.RunAsync`'s `batchSize`: the consecutive entries a batch covers at least,
  extended past a stream's inverted run; the class summary says the same
- [x] 3.3 `CHANGELOG.md` under `[4.4.0]` → Fixed: both defects, in the consumer's terms

## 4. The replay end to end

- [x] 4.1 A replay through the real replay worker over a real SQLite store whose sequence numbers run against its
  versions, with a batch boundary inside the inverted run, applies each stream in version order and every entry once
  (`tests/Stratara.Projections.Tests/Services/ProjectionReplayStreamOrderTests.cs`); fails with the worker reading
  plain sequence order
- [x] 4.2 The same over the PostgreSQL store
  (`tests/Stratara.Orleans.IntegrationTests/Projections/ProjectionReplayStreamOrderTests.cs`); same counter-check

## 5. Review follow-ups

- [x] 5.0a A portable batch holds at most 1,000 of its partition's entries before it is extended: the bound is the
  partition's thousandth entry within the key window, read without asking which entries are positioned
  (`PartitionCounterBackfill.BoundAsync`); a partition without an unpositioned entry is skipped before its walk
- [x] 5.0b A version-0 straggler is found: the floor's default is the lowest version, not `0`
  (`EventStreamRepositoryStreamOrderTests.A_stream_that_starts_at_version_zero_is_extended_to_its_first_version`)
- [x] 5.0c The walk condition is stated as a walk from `0` in the remarks and the CHANGELOG; the counter rows the
  backfill locks and updates ignore query filters too

## 6. Verification

- [ ] 6.1 `./scripts/local-gauntlet.sh` green
- [ ] 6.2 `tests/Stratara.Orleans.IntegrationTests` green
- [x] 6.3 `openspec validate read-and-prepare-a-long-history-in-linear-time --strict`
- [ ] 6.4 Independent review before the merge
