## 1. Bound the straggler search

- [ ] 1.1 The event stream repository's stream-order read bounds each stream's search from below by the highest
  version under the stream's top that lies at or before the range's start
  (`src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/EventSourcing/EventStreamRepository.cs`,
  `FindStragglerAsync`); `EventStreamRepositoryStreamOrderTests` (SQLite) and `ReplayStreamOrderTests`
  (PostgreSQL) pass unchanged
- [ ] 1.2 `PartitionCounterBackfill.FindStragglerAsync` takes the same bound over unpositioned entries
  (`src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PartitionCounterBackfill.cs`); `PartitionCounterBackfillTests`
  pass unchanged
- [ ] 1.3 `CommitTransactionIdBackfill`'s straggler statement takes the same bound over unstamped entries
  (`src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/CommitTransactionIdBackfill.cs`); `TransactionIdMigrationTests`
  pass unchanged
- [ ] 1.4 A PostgreSQL test over a long stream whose commits are partly numbered against their versions counts the
  rows the replay's read and each backfill touch, and asserts every stream's order and a bound linear in the store's
  entries (`tests/Stratara.Orleans.IntegrationTests/CommitOrder/LongStreamWorkTests.cs`); the counter-check —
  the old search restored — fails it
- [ ] 1.5 Benchmark before and after on two million entries with one stream holding a tenth, recorded in this file
  (scratch harness, not committed)

## 2. Query filters in the portable backfill

- [ ] 2.1 Every read and update of the event table in `PartitionCounterBackfill` ignores query filters
- [ ] 2.2 A test with a write context that applies the framework's tenant query filters positions every tenant's
  entries and the portable reader returns them — on PostgreSQL
  (`tests/Stratara.Orleans.IntegrationTests/CommitOrder/PartitionCounterBackfillTests.cs`) and on SQLite for the
  per-entry positioning; the counter-check fails both

## 3. Documentation

- [ ] 3.1 `IEventStreamRepository.GetManyAfterSequenceInStreamOrderAsync` remarks: the version order holds from `0`
  and from the highest sequence number of a returned result
  (`src/Stratara.Abstractions/Abstractions/EventSourcing/IEventStreamRepository.cs`)
- [ ] 3.2 `CommitTransactionIdBackfill.RunAsync`'s `batchSize`: the least a batch stamps, extended past a stream's
  inverted run; the class summary says the same
- [ ] 3.3 `CHANGELOG.md` under `[4.4.0]` → Fixed: both defects, in the consumer's terms

## 4. The replay end to end

- [ ] 4.1 A replay through the real replay worker over a real SQLite store whose sequence numbers run against its
  versions, with a batch boundary inside the inverted run, applies each stream in version order and every entry once
- [ ] 4.2 The same over the PostgreSQL store

## 5. Verification

- [ ] 5.1 `./scripts/local-gauntlet.sh` green
- [ ] 5.2 `tests/Stratara.Orleans.IntegrationTests` green
- [ ] 5.3 `openspec validate read-and-prepare-a-long-history-in-linear-time --strict`
- [ ] 5.4 Independent review before the merge
