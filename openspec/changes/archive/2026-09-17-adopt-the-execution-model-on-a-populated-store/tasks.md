## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it. This change is implemented after `close-the-round-5-execution-gaps`
      on the same branch line, and archived after it.
      Done: approved by the owner on 2026-09-16, recorded at the owner's request; implemented on the branch after the archive of `close-the-round-5-execution-gaps`.

## 1. The reader reports its head (D2)

- [x] 1.1 `ICommittedPositionReader.HeadAsync(int partition, CancellationToken)` with a default that walks
      batches from 0 and returns the last position; documented on the member. Verify:
      `src/Stratara.Abstractions/Abstractions/CommitOrder/ICommittedPositionReader.cs`; a unit test in
      `tests/Stratara.Orleans.Tests` on the default against a fake reader.
      Done: `HeadAsync` with a default that walks in batches of 1,000; `StoreReaderSeedingTests.The_default_head_walks_the_partition_to_its_last_position_and_is_zero_when_empty`.
- [x] 1.2 `PostgresTransactionIdReader` and `PortableCounterReader` override it with one query each, names
      from the model. Verify: the two readers; an integration test per reader in
      `tests/Stratara.Orleans.IntegrationTests/CommitOrder` (scenario *A reader reports its head*).
      Done: native: `MAX(commit_transaction_id)` below the snapshot horizon, names from the model; portable: `MAX(partition_position)` after refusing unpositioned entries. `ReaderHeadTests` (2 readers × 2 scenarios).

## 2. A host seeds its readers at the head (D1)

- [x] 2.1 `IStoreReaderSeeding` and `StoreReaderSeedingReport` in `Stratara.Orleans.Hosting`; the
      implementation seeds every registered consumer × partition without a checkpoint at the reader's
      head under the reader's name; registered scoped by the store reader core. Verify:
      `src/Stratara.Orleans/Hosting/IStoreReaderSeeding.cs`, `src/Stratara.Orleans/Projections/StoreReaderSeeding.cs`,
      `OrleansProjectionServiceCollectionExtensions.cs`; `SurfaceTests` updated; a unit test on the
      report with one seeded and one existing consumer (scenario *A consumer already has a checkpoint*).
      Done: `Hosting/IStoreReaderSeeding.cs`, `Projections/StoreReaderSeeding.cs`, registered scoped by the store reader core; `SurfaceTests` lists both types and the backfill; `StoreReaderSeedingTests` (seeded 5 / existing 1; a checkpoint under another reader is refused).
- [x] 2.2 A populated store seeded before the first start applies nothing old and everything new; a
      projection registered after the seeding reads from the beginning. Verify: an integration test in
      `tests/Stratara.Orleans.IntegrationTests/Projections` (scenarios *A populated store is seeded before
      the first start*, *A projection is added after the seeding*); run the first against 4.1.1 first and
      record that it fails there.
      Done: `SeededStartTests.A_seeded_first_start_applies_nothing_old_and_everything_new_and_a_late_projection_reads_from_the_beginning` (16 seeded, the two new facts applied, none of the three old; the late projection reads all five) and the baseline `Without_seeding_the_first_start_applies_the_history` — the port is new, so the 4.1.1 comparison is the baseline test, which applies the whole history.

## 3. A populated PostgreSQL store adopts the native reader (D3)

- [x] 3.1 `CommitTransactionIdBackfill.RunAsync(DbContext, int batchSize = 1_000, CancellationToken)` in
      `Stratara.Orleans.EntityFrameworkCore.CommitOrder`: null rows in sequence order, one transaction per
      batch, loops until none remain, reports the count, remarks say to run it while nothing appends.
      Verify: the class; `SurfaceTests`.
      Done: `CommitOrder/CommitTransactionIdBackfill.cs`: one `UPDATE … WHERE sequence IN (SELECT … WHERE column IS NULL ORDER BY sequence LIMIT batch)` per transaction until none is affected; names from the model; remarks name the window.
- [x] 3.2 A table populated before the column exists, migrated in three steps and backfilled, reads back in
      batches no larger than the backfill's and in append order per partition (scenario *A populated
      PostgreSQL store adopts the native reader*). Verify: an integration test in
      `tests/Stratara.Orleans.IntegrationTests/CommitOrder` that creates the table without the column,
      inserts, applies the three statements, runs the backfill, and reads from 0; against the unedited
      migration first, recording the single batch.
      Done: `TransactionIdMigrationTests`: the 4.1 table with the column dropped, 25 entries over three buckets; the three-step migration with a backfill batch of 10 reads back in batches ≤ 10 and in sequence order per partition; the unedited migration returns a partition's whole history under one transaction id when asked for 3.

## 4. Documentation (D4)

- [x] 4.1 `docs/guides/migrate-to-the-orleans-execution-model.md`: the populated-table subsection under
      "Migrate the schema" (the edited migration, the backfill, the window); "Upgrade in this order"
      before "Adopt the roles"; seeding as the step before the first start; the checkpoint key where the
      checkpoint table is introduced. Verify: the sections; documentation tests.
      Done: subsection *A populated event table on PostgreSQL* (three statements, the backfill call, the window), *Upgrade in this order* (six steps) before *Adopt the roles*, *Start on a populated store* (the seeding), the checkpoint key in the schema table. Documentation tests 703/703 (the snippets compile).
- [x] 4.2 `docs/guides/operate-the-orleans-execution-model.md`: seeding beside the reset, resolved from a
      scope, with the reset-first note. Verify: the section.
      Done: *Seeding instead of resetting* under the reset section.
- [x] 4.3 `docs/getting-started/prerequisites.md`: a row for the Orleans packages. Verify: the row.
      Done: a row for the two Orleans packages (membership and reminder tables, Redis 7+, PostgreSQL 15+).
- [x] 4.4 `src/Stratara.Orleans.EntityFrameworkCore/README.md` and `src/Stratara.Orleans/README.md` name the
      backfill and the seeding; `llms.txt`; `CHANGELOG.md` `[Unreleased]` *Added* for the three members
      and *Changed* for the documented migration. Verify: the entries; doc-symbol check.
      Done: both READMEs, `llms.txt`, `llms-full.txt` regenerated, CHANGELOG *Added* (seeding, `HeadAsync`, the backfill) and *Changed* (the documented migration and upgrade order); doc-symbol check clean.

## 5. Close

- [x] 5.1 `./scripts/local-gauntlet.sh` green; `CommitOrder` and `Projections` integration namespaces
      green against PostgreSQL. Verify: the run output, recorded here.
      Done 2026-09-17: gauntlet green (build 0 warnings, 21 unit suites 0 failures, Orleans unit tests 107/107,
      documentation tests 703/703, DocFX 0 warnings, doc-symbol and internal-reference checks clean);
      `CommitOrder` 28/28 and `Projections` 11/11 against PostgreSQL, Redis and RabbitMQ.
- [x] 5.2 `openspec validate adopt-the-execution-model-on-a-populated-store --strict` passes. Verify: the output.
      Done: "Change 'adopt-the-execution-model-on-a-populated-store' is valid".
