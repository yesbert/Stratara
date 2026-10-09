## Context

The read store belongs to the consumer. The framework's base `ReadDbContext<TContext>` contributes three
tables of its own — the tenant view, `projection_checkpoint` (Orleans store readers) and
`projection_forgotten_tenant` — and the consumer's derived context adds its views. There is no schema or
`search_path` setting in the framework; consumers set `HasDefaultSchema` themselves (at least one consumer does), so
every table name the context emits is schema-qualified. Projections inject whatever they write through
(`IProjectionsUnitOfWork`, `IDbContextFactory<T>`, …); the framework passes them nothing and does not see
their writes.

`ProjectionReplayWorker.RunReplayAsync` empties in two steps: `IForgottenTenantStore.ClearAsync` per
declaring projection, then the consumer-written `IProjectionViewTruncator.TruncateAllAsync` (the
framework ships no implementation; on Orleans `ReplayCheckpointResetTruncator` wraps it, pausing the store
readers and resetting their checkpoints around it). While the marking is active, publication is
suppressed (bus) and store readers are suspended (Orleans), so live writes into the read models largely
stop — except bundles that were already queued when the replay began.

After `keep-the-outcome-of-the-last-replay`, a replay has a request id, `TryActivate`/`Complete`, and an
outcome that survives it.

Evidence: the implementation at `main` 398d20d; consumer findings F-012 (2026-05-29 outage), F-015 (no full
rebuild run since 4.3.1 adoption), F-021 (2026-10-09 empty read store); the owner chose this approach over
build-beside-and-swap and over a hooks-only contract on 2026-10-09.

## Goals / Non-Goals

**Goals:**
- A replay on an opted-in host ends in one of two states: the rebuilt read models, or the ones it started
  from.
- A host that died mid-replay does not leave the read store partial past the next start.
- No change to the consumer's schema, index names, migrations or custom SQL objects — nothing that could
  drift from what the consumer's migrations expect.

**Non-Goals:**
- Readers seeing the old views during the rebuild. That needs the replay's writes redirected away from the
  live tables; with schema-qualified models it needs a per-scope model override, shadow DDL generated
  from the EF model (which omits whatever raw-SQL migrations created — pgvector indexes, views, functions),
  and a migrations-history table carried across the swap. Rejected for this change; it can be reconsidered
  on top of this one.
- Any provider other than PostgreSQL. The abstraction allows one; the framework ships one.
- Covering tables the consumer's truncator empties but the read context does not map. Documented, and
  configurable through `AdditionalTables`.

## Decisions

**Copy aside and write back, rather than rename aside.** Renaming each table away and recreating an empty
one (`CREATE TABLE … (LIKE … INCLUDING ALL)`) gives the new table generated index names, loses foreign keys
and leaves `serial` defaults pointing at the old table's sequences — the consumer's next migration then
fails on an index name it expects. Copying data (`CREATE TABLE copy AS TABLE live`) leaves the live tables,
their indexes and every object around them untouched; the price is writing the read store twice (once
aside, once back on failure) and holding a second copy on disk for the replay's duration.

**One consistent snapshot, in one transaction.** `PreserveAsync` opens a `REPEATABLE READ` transaction,
creates every copy in a dedicated schema (`ReadModelRestoreOptions.Schema`, default `stratara_replay`,
tables named `<schema>__<table>`, shortened with a hash past PostgreSQL's 63-byte identifier limit), and records
the marker row `(replay_id, preserved_at)` and one row per copy `(position, source, copy)` in the same
transaction. The restore reads what to write back from those rows, not from the model, so a model changed in
between cannot make it miss a copy. A store reader writes its view rows and its checkpoint in one transaction, so the snapshot
pairs them; the forgotten-tenant record is copied before the worker clears it. Preservation is the first
thing the worker does after `TryActivate`, before `ClearAsync` and the truncator, and the worker renews the
lease right after it (`SetProgress(0, 0)`), because preservation precedes the first progress report.

**Which tables.** Every table the read context's EF model maps (`IEntityType.GetTableName()` with its
schema; views, keyless and `ToSqlQuery` types excluded; table-sharing types deduplicated), minus
`ExcludedTables`, plus `AdditionalTables`. The framework's own three tables are included by construction.
At preservation the implementation reads `pg_constraint` for foreign keys from a table outside the set into
one inside it and fails before copying anything — the restore's `TRUNCATE` would refuse it. The same
catalog read yields the insertion order for the restore (topological over in-set foreign keys); a cycle
fails preservation too.

**Restore in one transaction.** `RestoreAsync(replayId)` takes `pg_advisory_xact_lock` on a fixed key,
re-reads the marker (another host may have restored already), compares each copy's columns with the live
table's (a migration that ran in between makes a blind write-back wrong — the restore then refuses, logs an
error and keeps the copy for the operator), `TRUNCATE`s every live table in one statement, inserts
parent-first with `OVERRIDING SYSTEM VALUE`, sets every owned sequence to the restored maximum (the
consumer's truncator may have used `RESTART IDENTITY`), drops the copies and the marker, and commits.
Readers block on the `TRUNCATE`'s lock for the duration and then see the restored state.

**The worker's endings.** Success → `DiscardAsync` (drop copies and marker in one transaction), then
`Complete(Succeeded)`. Failure → `RestoreAsync` with `CancellationToken.None`, then `Complete(Failed)` with
`ReadModelsRestored = true`; a restore that fails logs `104_1xx`, completes with `ReadModelsRestored =
false` and leaves the copy for the next start. Interruption → nothing; the copy is left for the next start.
A discard that fails is logged; the copy's marker then names a request whose outcome reads `Succeeded`,
which the abandoned check below treats as "drop, do not restore".

**Abandoned copies are found by asking the coordination state, under the database lock.**
`RestoreAbandonedAsync` runs once when the replay worker starts (after it has subscribed): with no marker
it returns. With one, it asks `GetProgress()` — a direct read of the coordination store, not the cached
flag — and restores only if no replay is active under the marker's request id and the last outcome for that
id is not `Succeeded`; a `Succeeded` outcome means a failed discard, and the copy is dropped. Two hosts
starting at once serialise on the advisory lock, and the second finds the marker gone. A replay requested
while a marker exists skips preservation and keeps the existing copy, re-pointing the marker at its own
request id in the same step that checks it.
- *Alternative:* a heartbeat column in the marker. Rejected: a second lease beside the one the
  coordination state already keeps, and one more write per batch.
- *Alternative:* restore only on the next request. Rejected: the read store would stay partial until an
  operator acts, which is the situation F-021 describes.

**The abstraction is `IReadModelPreservation` in `Stratara.Projections`.** Four members:
`PreserveAsync(Guid replayId, ct)`, `Task<bool> RestoreAsync(Guid replayId, ct)` (false when no copy is kept for
that replay — another host restored it first), `DiscardAsync(Guid replayId, ct)`, `Task<bool> RestoreAbandonedAsync(ct)`.
The outcome flag travels on `ReplayCompletion.ReadModelsRestored`, an `init` property of the record
`keep-the-outcome-of-the-last-replay` introduced for exactly this. Optional: the worker resolves it with `GetService` and, without it, does
exactly what it does today. `AddReadModelRestore<TReadContext>()` in `Stratara.EventSourcing.EntityFrameworkCore`
registers the PostgreSQL implementation and binds `ReadModelRestoreOptions` (section
`ProjectionReplay:Restore`).

## Risks / Trade-offs

- [Bundles already queued when the replay began are applied into the read models after the copy was taken;
  a restore discards their effect] → The window is the replay's first seconds: publication is suppressed
  from activation, and the bus drains what was queued. The guide states it; the copy is taken after the
  marking is active, so nothing published later is affected. A consumer that cannot accept it stops its
  projection consumers for the replay, as before.
- [The read store is written twice and held twice] → Opt-in, and the guide says what it costs in time and
  disk; the copy is dropped on success.
- [A migration runs between preservation and restore] → The restore compares columns and refuses rather
  than write rows into the wrong shape; the copy stays, logged with the replay id.
- [Restore holds an exclusive lock on every read model for the write-back's duration] → It runs only after
  a failure, and readers would otherwise see a partial store.
- [The consumer's truncator empties a table the read context does not map] → Not restored; documented,
  `AdditionalTables` covers it.
- [In-process coordination state after a host restart has no outcome] → An abandoned copy is then always
  restored, including after a discard that failed post-success; that sequence (discard fails, then the
  single host restarts) is logged at both steps.

## Migration Plan

Opt-in: `services.AddReadModelRestore<AppReadDbContext>()`. The `stratara_replay` schema is created on first
use; the database user needs `CREATE` on the database (or the schema created beforehand and named in
options). No migration of the consumer's read context. Rollback is removing the registration; a leftover
copy schema is inert and can be dropped.
