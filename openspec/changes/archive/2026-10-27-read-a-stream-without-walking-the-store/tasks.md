## 1. Red first

- [x] 1.1 *The stream reads carry the partition*: for each of `StreamExistsAsync`, `GetFirstOrDefaultAsync`,
  `GetManyAsync` (with and without a version range) and `GetVersionOrDefaultAsync`, and for `SnapshotRepository.GetAsync`
  and `GetLatestVersionOrDefaultAsync`, assert that the generated SQL conditions on the bucket column with the
  value `BucketCalculator.GetBucketId(streamId)` (through `ToQueryString` on a SQLite or Npgsql model, or a command
  interceptor). Run it against the unchanged code and record that it fails
  (`tests/Stratara.EntityFrameworkCore.Tests/StreamReadPartitionTests.cs`, spec *Reading one stream costs what the
  stream holds*)
- [x] 1.2 *The indexes under both keys*: a write context on Npgsql with snake_case naming that applies
  `ApplyGlobalEntityConfiguration()` declares `ix_event_stream_entry_sequence_number` (unique, `[SequenceNumber]`),
  `ix_event_stream_entry_unhashed_sequence_number` (`[SequenceNumber]`, filtered) and
  `ix_event_chain_anchor_sequence_number` (non-unique). A context that keeps the framework's key declares no index
  over `[SequenceNumber]` alone besides the filtered one. A context that overrides `ConfigureConventions` without
  calling `base` lacks the two event-stream indexes. That case pins the documented requirement, it does not assert
  a fix. The convention declares both indexes with the precedence of a data annotation: EF Core drops a
  convention-sourced index over the same column as another as redundant, which the first green run showed. Record
  that the first two fail against the unchanged code
  (`tests/Stratara.EntityFrameworkCore.Tests/StoreSchemaAdditionsTests.cs`, spec scenarios *A consumer applies the
  framework's entity-key convention…* and *A consumer keeps the framework's key…*)
- [x] 1.3 *The filter follows the column name*: the filter is exactly `hash IS NULL` under snake_case Npgsql and
  `"Hash" IS NULL` under a PascalCase Npgsql context, and every index's database name is the fixed name under both
  (`StoreSchemaAdditionsTests.cs`, design D3)
- [x] 1.4 *The plans on PostgreSQL*: on a PostgreSQL 17 write context that applies `ApplyGlobalEntityConfiguration()`,
  seeded with enough streams that the planner prefers an index (and `ANALYZE`d), `EXPLAIN` of the generated SQL of
  each stream and snapshot read from 1.1, of `GetUnhashedEventsAsync`, `GetPreviousEventAsync`,
  `GetLastHashedEventAsync`, `GetManyAfterSequenceAsync`, `GetMaxSequenceNumberAsync` and
  `EventChainRepository.GetLastSequenceNumberOrDefaultAsync([])` contains no `Seq Scan` on the event, snapshot or
  anchor table and no full-index scan of the `(bucket_id, stream_id, version)` index. Run it once with the
  PascalCase context of `NativeReaderModelNamesTests` as well, which also applies the migration with the quoted
  filter. Record that it fails against the unchanged code
  (`tests/Stratara.Orleans.IntegrationTests/EventStore/EventStoreLookupPlanTests.cs`, spec scenarios *An aggregate
  is loaded from a large store*, *A pass runs on a large chained store*, *Many anchors have been recorded*)
- [x] 1.5 *Adopting a bridge is empty*: a model that declares HiveWeaver's three indexes under the documented names,
  diffed with `IMigrationsModelDiffer` against the framework's model with those declarations removed, yields no
  operation on the event stream's or the anchors' sequence-number indexes
  (`StoreSchemaAdditionsTests.cs`, spec scenario *A consumer that bridged the indexes adopts the framework's*).
  Result: 1.1 to 1.5 failed against the unchanged code, 1.1 on the missing bucket condition, 1.2, 1.3 and 1.5 on
  the missing indexes, and 1.4 on a sequential scan of the unchained entries and on an index condition without
  the bucket

## 2. Stream and snapshot reads

- [x] 2.1 The four single-stream reads condition on `BucketId == BucketCalculator.GetBucketId(streamId)` as well,
  computed once per call. Order and projection stay as they are
  (`src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/EventSourcing/EventStreamRepository.cs`, design D1)
- [x] 2.2 The same for both snapshot reads
  (`src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/EventSourcing/SnapshotRepository.cs`, design D1)
- [x] 2.3 The existing event-source and snapshot tests stay green: what the reads return does not change
  (`tests/Stratara.Infrastructure.Tests`, `tests/Stratara.EntityFrameworkCore.Tests`). Result: two fixtures wrote
  a stream's rows under a partition its id does not give, `BucketId = 0` and `BucketId = 3`, and failed. They now
  write as the framework does (`SnapshotOwnerTests`, `tests/Stratara.WriteStore.Tests/TenantFilteredStoreReadTests`)

## 3. Indexes

- [x] 3.1 An internal model-finalizing convention declares the conditional unique index and the filtered index on the
  event stream. It gets the filter's column name from the model and the provider's `ISqlGenerationHelper`, and
  declares no filtered index where the provider is not relational. Each index carries the fixed name both as its
  model name and as its database name
  (`src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/EventSourcing/Configurations/SequenceNumberIndexConvention.cs`,
  design D2 and D3)
- [x] 3.2 `WriteDbContext<TContext>` overrides `ConfigureConventions`, calls `base` and adds the convention. Its XML
  remarks say a derived context that overrides `ConfigureConventions` calls `base`
  (`src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/WriteDbContext.cs`)
- [x] 3.3 `EventChainAnchorConfiguration` declares the non-unique index `ix_event_chain_anchor_sequence_number` and
  updates its XML summary (`Configurations/EventChainAnchorConfiguration.cs`, design D4)
- [x] 3.4 Update the XML summary of `EventStreamEntryConfiguration` to say where the sequence-number indexes come
  from (`Configurations/EventStreamEntryConfiguration.cs`)
- [x] 3.5 Check the integration-test write contexts with checked-in migrations or `EnsureCreated`
  (`TransactionIdMigrationTests`, `NativeReaderModelNamesTests`, `PartitionCounterModelNamesTests`,
  `src/Stratara.Testing.EntityFrameworkCore`) for a pending-model-changes failure, and update the ones that need it.
  Result: no project in the repository checks in migrations; tests and samples build their schema with
  `EnsureCreated`, so none needs updating

## 4. Green

- [x] 4.1 1.1 to 1.5 pass
- [x] 4.2 The touched integration classes stay green: `CommitOrder/*` (the readers that read in sequence order),
  `Aggregates/ConditionalAppendTests`, `Aggregates/LongOrderTests`, and the chaining tests of
  `tests/Stratara.Infrastructure.Tests` (`EventStreamHashService`, `EventChainService`). Result: 58 of 58 integration
  tests green, and `Stratara.Infrastructure.Tests`, `Stratara.WriteStore.Tests` and `Stratara.EntityFrameworkCore.Tests`
  green

## 5. Documentation and release notes

- [x] 5.1 `docs/getting-started/di-composition.md`, *The store declares its own schema*: add the three indexes to the
  table with their names and what they serve, and state that they hold under `ApplyGlobalEntityConfiguration()`.
  Say that a derived context that overrides `ConfigureConventions` calls `base`. Add a short paragraph on creating
  them on a large store with `CREATE INDEX CONCURRENTLY` in a migration that suppresses its transaction (design D5)
- [x] 5.2 `docs/concepts/performance-and-scaling.md`, *Horizontal scaling via stream buckets*: the bullet on indexed
  partition scans says that reading one stream is confined to its partition, and the sequence-order and chaining
  reads have indexes of their own
- [x] 5.3 Check `llms.txt`, `llms-full.txt`, `docs/concepts/tamper-evident-streams.md` and `docs/overview/glossary.md`
  for a claim this change contradicts. Result: none of them describes the indexes or the stream reads
- [x] 5.4 `CHANGELOG.md`: an entry under `[Unreleased]`, *Fixed*, opening with **Generate an EF Core migration for the
  write context**, then naming the three indexes and that a host that declared them itself under those names
  removes its declarations first. The CHANGELOG names no consumer findings, so SF-012 is named in the proposal and
  the pull request instead

## 5a. Review findings (`/code-review high`; Copilot's quota was exhausted)

- [x] 5a.1 Rows written before 3.0.0 lie under another partition (design D6): a session-scoped SQL function equal to
  `BucketCalculator.GetBucketId`, a check and a repair in `docs/getting-started/di-composition.md`, the check first
  in the CHANGELOG's upgrade steps, and the spec scenario *A store holds rows a build before the first public
  release wrote* (`tests/Stratara.Orleans.IntegrationTests/EventStore/StreamPartitionRepairTests.cs`)
- [x] 5a.2 The plan test names the index each read must use, so a missing or misfiltered partial index fails it
  (`EventStoreLookupPlanTests`)
- [x] 5a.3 The guide's concurrent-index migration drops `IF NOT EXISTS`, which would hide an invalid index left by a
  failed concurrent build, and says how to recover from one
- [x] 5a.4 The anchor index constant is `AnchorSequenceNumberIndex`, distinct from the event stream's; the usings of
  `WriteDbContext` are sorted
- [x] 5a.5 Answered without a change: a derived context that overrides `ConfigureConventions` without `base` lacks the
  indexes (documented and pinned, design D2 on why an options extension would miss the design-time model); a
  consumer convention that renames the hash column after ours runs is not supported, since a context that adds
  conventions after `base` decides their order; an explicit `IsUnique(false)` of the consumer outranks the
  framework's index of the same name, and an index of the consumer's own name over the sequence number is the
  consumer's to rename (the guide names the framework's names)

## 6. Verification

- [x] 6.1 `openspec validate read-a-stream-without-walking-the-store --strict`
- [x] 6.2 `./scripts/local-gauntlet.sh` green
- [x] 6.3 The integration classes of 1.4 and 4.2 green locally (Docker). The full suite runs through `integration.yml`
  after the merge
