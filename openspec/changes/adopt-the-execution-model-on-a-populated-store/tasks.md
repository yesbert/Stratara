## 0. Gate

- [ ] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it. This change is implemented after `close-the-round-5-execution-gaps`
      on the same branch line, and archived after it.

## 1. The reader reports its head (D2)

- [ ] 1.1 `ICommittedPositionReader.HeadAsync(int partition, CancellationToken)` with a default that walks
      batches from 0 and returns the last position; documented on the member. Verify:
      `src/Stratara.Abstractions/Abstractions/CommitOrder/ICommittedPositionReader.cs`; a unit test in
      `tests/Stratara.Orleans.Tests` on the default against a fake reader.
- [ ] 1.2 `PostgresTransactionIdReader` and `PortableCounterReader` override it with one query each, names
      from the model. Verify: the two readers; an integration test per reader in
      `tests/Stratara.Orleans.IntegrationTests/CommitOrder` (scenario *A reader reports its head*).

## 2. A host seeds its readers at the head (D1)

- [ ] 2.1 `IStoreReaderSeeding` and `StoreReaderSeedingReport` in `Stratara.Orleans.Hosting`; the
      implementation seeds every registered consumer × partition without a checkpoint at the reader's
      head under the reader's name; registered scoped by the store reader core. Verify:
      `src/Stratara.Orleans/Hosting/IStoreReaderSeeding.cs`, `src/Stratara.Orleans/Projections/StoreReaderSeeding.cs`,
      `OrleansProjectionServiceCollectionExtensions.cs`; `SurfaceTests` updated; a unit test on the
      report with one seeded and one existing consumer (scenario *A consumer already has a checkpoint*).
- [ ] 2.2 A populated store seeded before the first start applies nothing old and everything new; a
      projection registered after the seeding reads from the beginning. Verify: an integration test in
      `tests/Stratara.Orleans.IntegrationTests/Projections` (scenarios *A populated store is seeded before
      the first start*, *A projection is added after the seeding*); run the first against 4.1.1 first and
      record that it fails there.

## 3. A populated PostgreSQL store adopts the native reader (D3)

- [ ] 3.1 `CommitTransactionIdBackfill.RunAsync(DbContext, int batchSize = 1_000, CancellationToken)` in
      `Stratara.Orleans.EntityFrameworkCore.CommitOrder`: null rows in sequence order, one transaction per
      batch, loops until none remain, reports the count, remarks say to run it while nothing appends.
      Verify: the class; `SurfaceTests`.
- [ ] 3.2 A table populated before the column exists, migrated in three steps and backfilled, reads back in
      batches no larger than the backfill's and in append order per partition (scenario *A populated
      PostgreSQL store adopts the native reader*). Verify: an integration test in
      `tests/Stratara.Orleans.IntegrationTests/CommitOrder` that creates the table without the column,
      inserts, applies the three statements, runs the backfill, and reads from 0; against the unedited
      migration first, recording the single batch.

## 4. Documentation (D4)

- [ ] 4.1 `docs/guides/migrate-to-the-orleans-execution-model.md`: the populated-table subsection under
      "Migrate the schema" (the edited migration, the backfill, the window); "Upgrade in this order"
      before "Adopt the roles"; seeding as the step before the first start; the checkpoint key where the
      checkpoint table is introduced. Verify: the sections; documentation tests.
- [ ] 4.2 `docs/guides/operate-the-orleans-execution-model.md`: seeding beside the reset, resolved from a
      scope, with the reset-first note. Verify: the section.
- [ ] 4.3 `docs/getting-started/prerequisites.md`: a row for the Orleans packages. Verify: the row.
- [ ] 4.4 `src/Stratara.Orleans.EntityFrameworkCore/README.md` and `src/Stratara.Orleans/README.md` name the
      backfill and the seeding; `llms.txt`; `CHANGELOG.md` `[Unreleased]` *Added* for the three members
      and *Changed* for the documented migration. Verify: the entries; doc-symbol check.

## 5. Close

- [ ] 5.1 `./scripts/local-gauntlet.sh` green; `CommitOrder` and `Projections` integration namespaces
      green against PostgreSQL. Verify: the run output, recorded here.
- [ ] 5.2 `openspec validate adopt-the-execution-model-on-a-populated-store --strict` passes. Verify: the output.
