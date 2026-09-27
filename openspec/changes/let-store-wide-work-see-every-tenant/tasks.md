## 1. Ignore the filters in store-wide reads

- [ ] 1.1 `EventStreamRepository`: the store-wide members ignore query filters, and the per-stream members do not
  (`src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/EventSourcing/EventStreamRepository.cs`)
- [ ] 1.2 `EventChainRepository.GetLastSequenceNumberOrDefaultAsync` ignores them
- [ ] 1.3 `PortableCounterReader`, `PortableCounterStartupCheck` and `PostgresTransactionIdReader` ignore them
  (`src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/`)
- [ ] 1.4 Remarks on the store-wide members of `IEventStreamRepository` and `IEventChainRepository`

## 2. Tests with a filtered write context

- [ ] 2.1 SQLite, write store: the replay's reads, the hash chain's reads and the anchor read return every tenant's
  entries; a per-stream read under another tenant returns none (`tests/Stratara.WriteStore.Tests`)
- [ ] 2.2 SQLite, execution model: the portable reader returns every tenant's entries and its start check refuses an
  unpositioned entry of any tenant (`tests/Stratara.Testing.Orleans.Tests`)
- [ ] 2.3 PostgreSQL: the native reader returns every tenant's entries (`tests/Stratara.Orleans.IntegrationTests`)
- [ ] 2.4 Counter-check: with the filters honoured, 2.1–2.3 fail

## 3. Documentation

- [ ] 3.1 `docs/guides/enforce-tenant-isolation.md`: what a filter on the write context reaches and what it does not
- [ ] 3.2 `CHANGELOG.md` under `[4.4.0]` → Fixed

## 4. Verification

- [ ] 4.1 `./scripts/local-gauntlet.sh` green
- [ ] 4.2 `tests/Stratara.Orleans.IntegrationTests` green
- [ ] 4.3 `openspec validate let-store-wide-work-see-every-tenant --strict`
- [ ] 4.4 Independent review before the merge
