## 1. Read the store past the filters

- [x] 1.1 `EventStreamRepository`: every read ignores query filters, about one stream or across the store
  (`src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/EventSourcing/EventStreamRepository.cs`)
- [x] 1.2 `EventChainRepository.GetLastSequenceNumberOrDefaultAsync` and both `SnapshotRepository` reads ignore them
- [x] 1.3 `PortableCounterReader`, `PortableCounterStartupCheck` and `PostgresTransactionIdReader` ignore them
  (`src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/`)
- [x] 1.4 `IEventStreamRepository`, `ISnapshotRepository` and `IEventChainRepository` say at type level that every
  read returns every tenant's rows

## 2. Tests with a filtered write context

- [x] 2.1 SQLite, write store: the replay's reads, the hash chain's reads and the anchor read return every tenant's
  entries without a session; the per-stream reads and both snapshot reads return another tenant's stream under a
  session (`tests/Stratara.WriteStore.Tests/TenantFilteredStoreReadTests.cs`)
- [x] 2.2 SQLite, execution model: the portable reader returns every tenant's entries and its start check refuses an
  unpositioned entry of any tenant (`tests/Stratara.Testing.Orleans.Tests/TenantFilteredPortableReaderTests.cs`)
- [x] 2.3 PostgreSQL: the native reader returns every tenant's entries
  (`tests/Stratara.Orleans.IntegrationTests/CommitOrder/TenantFilteredNativeReaderTests.cs`)
- [x] 2.5 SQLite, event source: a stream with events of two owners, snapshotted after every save, is rebuilt in
  full under either owner's session and without one, found without one, and appended to by its owner
  (`tests/Stratara.Testing.EntityFrameworkCore.Tests/TenantFilteredStreamTests.cs`)
- [x] 2.4 Counter-check: with the filters honoured, 2.1–2.3 and 2.5 fail (SQLite done: with `src/` as on the base
  every store-wide test fails; with the first cut, per-stream reads filtered, 2.1's per-stream test and 2.5 fail)

## 3. Documentation

- [x] 3.1 `docs/guides/enforce-tenant-isolation.md`: a filter on the write context applies to the consumer's own
  queries; the framework reads its store past every filter; isolation stays at the entrance
- [x] 3.2 `CHANGELOG.md` under `[4.4.0]` → Fixed, and → Changed for direct callers of the three repositories

## 4. Verification

- [x] 4.1 `./scripts/local-gauntlet.sh` green
- [x] 4.2 `tests/Stratara.Orleans.IntegrationTests` green
- [x] 4.3 `openspec validate let-store-wide-work-see-every-tenant --strict`
- [x] 4.4 Independent review before the merge
