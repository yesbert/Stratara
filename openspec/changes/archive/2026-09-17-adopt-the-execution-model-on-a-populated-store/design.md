## Context

See `proposal.md` — Why. The current code:

- **Checkpoints.** `IProjectionCheckpointStore.GetAsync` (`src/Stratara.Abstractions/Abstractions/Projections/IProjectionCheckpointStore.cs:17`)
  returns 0 where no row exists; `StoreReaderLoop.CatchUpAsync` (`src/Stratara.Orleans/Projections/StoreReaderLoop.cs:163-167`)
  reads after it. The consumer names a host registers are known to the composition through the
  internal nudge targets (`ProjectionGrain.cs:191-198`), which `ExecutionModelReset` already uses to
  scope its delete (`src/Stratara.Orleans.EntityFrameworkCore/Hosting/ExecutionModelReset.cs:33-45`).
  The consumer name is the projection class's simple name (`src/Stratara.Projections/Services/ProjectionHandler.cs:26`);
  sagas checkpoint as one consumer.
- **Reader port.** `ICommittedPositionReader` (`src/Stratara.Abstractions/Abstractions/CommitOrder/ICommittedPositionReader.cs`)
  has `Name` and `ReadAfterAsync(partition, afterPosition, batchSize, ct)` returning a `CommittedBatch`
  with `Position` and `HasMore`. The native reader filters `commit_transaction_id < pg_snapshot_xmin(pg_current_snapshot())`
  and orders by transaction id then sequence (`PostgresTransactionIdReader.cs:143-150`); the portable
  reader orders by `partition_position`.
- **Column.** `CommitOrderSchema.ApplyPostgresTransactionId` (`src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/CommitOrder/CommitOrderSchema.cs:31-34`)
  declares `xid8`, `HasDefaultValueSql("pg_current_xact_id()")`, non-null. EF Core's migration for an
  existing table is `ADD COLUMN … xid8 NOT NULL DEFAULT (pg_current_xact_id())`; PostgreSQL rewrites
  the table for a volatile default and every existing row receives the `ALTER`'s transaction id. The
  reader's batch cut never splits a transaction and falls back to reading the whole transaction
  (`PostgresTransactionIdReader.cs:78-96`), so one transaction spanning the history is one batch.
- **Backfill pattern.** `PartitionCounterBackfill.RunAsync(DbContext, CommitOrderOptions, ct)`
  (`src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PartitionCounterBackfill.cs`) is the shipped
  one-time helper for the portable reader: a static class, a context derived from the write context,
  batches of 1,000, names taken from the model.

## Goals / Non-Goals

**Goals:**
- A team with current read models adopts the projection and saga roles without applying history, by
  one documented step it runs once.
- A team with a populated PostgreSQL event table adopts the native reader without a table rewrite,
  and its history reads back in bounded batches in append order.
- The guide states the rolling order and the checkpoint key.

**Non-Goals:**
- Seeding per projection or at an arbitrary position; the helper seeds every registered consumer at
  the head. A team that wants one projection rebuilt seeds, then rebuilds that projection.
- Making the transaction-id column nullable in the model, or teaching the native reader to stop at a
  null (the portable reader's shape). The column stays non-null with its default; the migration on a
  populated table is a documented edit of the generated migration plus the backfill.
- Any change to the readers' ordering or to the checkpoint store's schema.

## Decisions

### D1 — A seeding port beside the reset, scoped to the host's registered consumers

`IStoreReaderSeeding` in `Stratara.Orleans.Hosting`, with `SeedAtHeadAsync(CancellationToken)` returning
`StoreReaderSeedingReport(int Seeded, int Existing)`. The implementation lives in `Stratara.Orleans`
(it needs no EF): it takes the registered nudge targets' consumer names, the registered
`ICommittedPositionReader` and `IProjectionCheckpointStore`, asks the reader for each partition's head
once, and for every consumer × partition without a checkpoint writes the head under the reader's name.
"Without a checkpoint" is decided by the store: the port gains no member; the seeding reads the
position and treats 0 as absent, which is what the loop does — a consumer with a genuine checkpoint at
0 has applied nothing and loses nothing by being seeded. It is registered scoped by the store reader
core (`AddStrataraProjectionGrains` / `AddStrataraSagaGrains`), where the nudge targets are, and the
guide resolves it from a scope as it does the reset.

*Rejected: an option on the registrations ("missing checkpoint = head").* Owner decision 2026-09-16; a
projection added a year later would skip its history silently.
*Rejected: seeding inside the reset.* A reset means "nothing remembered"; seeding means "remember the
head". Two verbs, two ports, same shape.
*Rejected: a `TryGet` on the checkpoint port.* Public surface for a distinction the loop itself does
not make.

Evidence: `ExecutionModelReset.cs:33-45` (the consumer-name scoping to mirror); `ProjectionCheckpointStore.cs:19`;
`ResetTests.cs` (the composition a test will reuse). Test: seed a populated store, start, assert the
probe saw no old entry and every new one.

### D2 — The reader port reports its head, with a default that walks

`ICommittedPositionReader` gains `Task<long> HeadAsync(int partition, CancellationToken)` with a default
interface implementation that reads batches from 0 until `HasMore` is false and returns the last
position (0 on an empty partition) — correct for any reader, and slow only for a consumer's own
reader that does not override. The native reader overrides with
`SELECT max(commit_transaction_id) WHERE bucket % n = p AND commit_transaction_id < pg_snapshot_xmin(pg_current_snapshot())`,
the portable reader with `SELECT max(partition_position) WHERE bucket % n = p`. Both are the position
a read now would end on, so a read after it returns exactly what commits later.

*Rejected: computing the head in the seeding from the readers' storage.* The seeding would then know
both readers' columns; the head is the reader's to say.
*Rejected: an abstract member.* Breaks a consumer's own reader at compile time for one method.

Evidence: `ICommittedPositionReader.cs:36`; `PostgresTransactionIdReader.cs:143-150`;
`PortableCounterReader.cs` (its statement); `InterleavedCommitTests.cs` (the fixture the head test
reuses). Test per reader: head, append, read after head returns the append and nothing before.

### D3 — The column is added without a rewrite; the backfill stamps history in append order, in batches

The guide's "Migrate the schema" gains a subsection for a populated PostgreSQL table: edit the generated
migration so that the column is added nullable without a default (a metadata-only change), then run
`CommitTransactionIdBackfill.RunAsync(DbContext, batchSize = 1_000, ct)`, then in the same migration
or a second one set the default `pg_current_xact_id()` and `NOT NULL`. The backfill selects entries
whose column is null in sequence order, in batches, and updates each batch in a transaction of its own
with `SET commit_transaction_id = pg_current_xact_id()`, so each batch receives one ascending
transaction id and the reader sees history in batch-sized groups ordered as appended. It loops until
no null remains, reports how many it stamped, and is idempotent. It runs while nothing appends: an
append during the backfill would receive a null (before the default) and a later id than newer rows
(after), which inverts version order within a stream; the guide names the window and the helper's
remarks repeat it.

Why not keep EF's migration and accept the single transaction: on a populated table it holds an
exclusive lock for a rewrite, and afterwards every rebuild reads the whole partition in one batch.

*Rejected: a nullable model column with a null-stop in the native reader.* It adds an existence query
to every native read for a one-time migration, and the portable reader's null-stop exists because
appends can be unpositioned at any time; for the native reader they cannot.
*Rejected: stamping history with synthetic ascending ids below the current xmin.* There is no cast
into `xid8` from an integer the framework controls, and a value the database did not assign is a
value the snapshot logic never promised.
*Rejected: EF's `HasDefaultValueSql` removed and the id set by an interceptor.* The database's own
assignment on insert is what makes the native reader add no work to an append.

Evidence: `CommitOrderSchema.cs:31-34`; PostgreSQL documentation on `ADD COLUMN` with a volatile
default (rewrite) versus none (metadata only); `PostgresTransactionIdReader.cs:78-110`;
`PartitionCounterBackfill.cs` (the helper's shape). Test: a table populated before the column exists,
the three-step migration applied by the test, the backfill run, then a read from 0 asserting batch
sizes ≤ the backfill's batch and sequence order within each partition.

### D4 — The rolling order is one section, in the order a team runs it

The guide's "Adopt the roles" is preceded by "Upgrade in this order": migrate the schema (populated
table: D3) before the first 4.1 host; 4.0 hosts stay up (every addition nullable or defaulted — stated
as a fact with the column list); stop the bus outbox worker before the first drain silo with an intent
store; seed (D1); start the silos; switch the API host; let the command queue drain; stop the bus
projection and saga workers; delete the queues nothing consumes. The checkpoint key is stated where
the checkpoint table is introduced. The prerequisites page gains a row for the two Orleans packages
(PostgreSQL 15+ as the store already requires, Redis 7+ for the directory, the Orleans runtime tables).

Evidence: `docs/guides/migrate-to-the-orleans-execution-model.md:44-60,95-115`; `OutboxEntry.cs:33-48`
and `EventStreamEntryConfiguration.cs` (nullable/defaulted additions).

## Risks / Trade-offs

- [Seeding on a store with a checkpoint at 0 that was meant as "start over"] → the seeding writes the
  head; the guide says to run the reset first where a full re-read is wanted.
- [The default `HeadAsync` walks the whole store for a consumer's own reader] → documented on the
  member; both shipped readers override.
- [A team runs the backfill while a host appends] → version order inside a stream can invert for the
  rows appended in the window; the guide and the helper's remarks say to stop appends, and the
  backfill's report lets the team confirm the count matches the rows that existed.
- [A team applies EF's generated migration unedited] → it works, with the lock and the single
  transaction the proposal describes; the guide says which line to edit and why.

## Migration Plan

Patch release. New public surface: `IStoreReaderSeeding` and its report, `CommitTransactionIdBackfill`,
`ICommittedPositionReader.HeadAsync` with a default. No schema change. Rollback: nothing to undo; a
seeded checkpoint is an ordinary checkpoint row.
