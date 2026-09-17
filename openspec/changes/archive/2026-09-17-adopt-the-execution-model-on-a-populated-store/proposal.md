# Adopt the execution model on a populated store

> **Status:** approved (owner, 2026-09-16 — recorded at the owner's request)

## Why

The second readiness audit of 2026-09-16 walked the migration guide as a team with a running
deployment would — a populated event store, read models that are current, sagas that have already
reacted — and found that the guide leads such a team into re-applying its whole history:

- **A store reader without a checkpoint starts at the beginning of the store.** Nothing lets a host
  say "my read models are current; start at the head". A team that adopts the projection role sees
  every counter doubled and every unique key violated, and a team that adopts the saga role sees every
  side-effect of its history fired again — as recorded commands now, run in grains. The only way out
  today is to insert checkpoint rows by hand, per consumer and partition, under the reader's name, at a
  position the team has to compute from the database.
- **The native reader's transaction-id column rewrites the event table and stamps the whole history
  with one transaction.** The framework declares it non-null with a volatile default, so the migration
  EF Core generates holds an exclusive lock for the duration of a full table rewrite, and every
  existing row receives the migration's own transaction id. The reader never ends a batch inside one
  transaction, so the first read of each partition — and every rebuild afterwards — loads the entire
  pre-migration history of that partition in one batch and applies it in one scope. Neither the
  guide nor the changelog mentions the lock, the rewrite or the consequence.
- **The order of a rolling upgrade is unstated.** The schema must be migrated before the first 4.1
  host starts; 4.0 hosts keep running against the migrated schema, because every addition is nullable
  or defaulted; the roles switch in an order that lets no command run on both paths; and the bus
  queues left without a consumer must be deleted. None of it is written down, and the checkpoint's
  key — the projection class's simple name — is not documented either, so a renamed projection
  silently starts its history over.

## What Changes

- **A host can seed its store readers at the head.** A port beside the reset writes, for every
  store-reading projection and saga the host registers and every partition, a checkpoint at the
  store's current head where none exists, under the reader's name, and reports how many it wrote and
  how many already existed. It runs once, while no silo runs, before the host's first start on a
  populated store. A consumer that already has a checkpoint is untouched; a projection added later
  still starts at the beginning, which is what a new projection needs.
- **The reader offers its head.** The commit-order reader port gains a member returning the position
  after which nothing committed exists at the time of the call, with a default that walks the store so
  a consumer's own reader keeps compiling; both shipped readers answer it in one query.
- **A populated PostgreSQL store adopts the native reader without a rewrite.** The migration guide
  gives the three-step migration — add the column nullable, backfill, set the default and the
  constraint — and the framework ships the backfill: it stamps the history in append order, in
  batches, each under a transaction of its own, so the reader sees history in bounded batches and in
  the order it was appended. It runs while nothing appends.
- **The rolling upgrade is documented**: schema before the first 4.1 host; 4.0 hosts stay up; seed,
  then silos, then the API host; what to do with the bus queues; and that the checkpoint is keyed by
  the projection's simple class name. The prerequisites page gets an Orleans row.
- **Consumer-visible effects:** two new public members (the seeding port and the backfill helper), one
  new member with a default on a public port. No schema change. No behaviour changes for a host that
  does not call them. Versioning: patch.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`: *Projections and sagas read the store in commit order and never miss a
  committed fact* — a host can seed the checkpoints of the consumers it registers at the head before
  its first start; new scenarios.
- `event-sourcing-store`: *The store can be read in commit order without skipping a late committer* —
  a reader reports its head; a populated PostgreSQL store adopts the native reader by the documented
  migration and the shipped backfill, which orders history as appended in bounded batches; new
  scenarios.

## Impact

- `Stratara.Abstractions` — `ICommittedPositionReader` gains a member with a default implementation.
- `Stratara.Orleans` — the seeding port beside `IExecutionModelReset`, registered with the store
  reader roles.
- `Stratara.Orleans.EntityFrameworkCore` — the head query on both readers; the transaction-id backfill
  beside `PartitionCounterBackfill`.
- `docs/guides/migrate-to-the-orleans-execution-model.md` (schema migration on a populated store,
  seeding, rolling order), `docs/guides/operate-the-orleans-execution-model.md` (the seeding beside
  the reset), `docs/getting-started/prerequisites.md` or its equivalent, `CHANGELOG.md`, `llms.txt`,
  `src/Stratara.Orleans.EntityFrameworkCore/README.md`.
- Tests: seeding on a populated store followed by a first start that applies nothing old and
  everything new; the head of each reader; the backfill on a populated table read in bounded batches
  and in append order; documentation tests.
- Versioning: patch (4.1.2, with `close-the-round-5-execution-gaps`).
