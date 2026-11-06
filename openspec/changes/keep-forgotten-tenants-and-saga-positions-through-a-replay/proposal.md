# Keep forgotten tenants out of the read models and saga positions in place through a replay

> **Status:** approved (owner, 2026-11-06)

## Why

A consumer that declares `IForgetsDeletedTenants` still finds a deleted tenant's rows in its read models after a
full replay, and a host's truncator written the way `IProjectionViewTruncator` recommends makes every saga run again
from the beginning. Both were measured by a consumer team and reported as finding F-034: rebuilds of a local stack
with 1195 facts and eight deleted tenants left a view with 3 rows before them and 8, 5, 5 and 7 after them, and one
rebuild whose truncator emptied the saga checkpoints issued 1028 commands and 790 facts more than it should have.

The first happens because a fact that adds a row reports nothing, so the record of deleted tenants is never asked,
and store readers partition by stream, so a tenant's deletion can be read before a fact of that tenant on another
stream. The second happens because the saga positions live beside the projections' in the read store, and the
contract's "`TRUNCATE ... CASCADE` across the read-store schema" takes them too.

## What Changes

What a consumer notices:

- A projection that declares `IForgetsDeletedTenants` is no longer called for a fact whose owning tenant it has
  recorded as deleted. The fact is passed over and logged as a passed-over fact already is, whether or not the
  projection would have reported a missing prerequisite. The two tenant-deletion facts themselves are always handled.
  This reverses the published sentence that such a fact is "applied as usual".
- Such a projection pays one read of the record for each fact it is handed, other than a deletion fact or a fact with
  an empty tenant, measured at about 330 to 560 µs per fact on a local PostgreSQL. A projection that does not declare the interface pays nothing and behaves as before.
- A fact of a deleted tenant that removes something elsewhere is passed over too. That is a documented limit of the
  declaration.
- The `IProjectionViewTruncator` contract says what must survive a truncation, the checkpoint rows of the saga
  consumers, and no longer recommends a cascade across the schema.
- On a host whose projections read the store, a full replay remembers the saga positions before the host's truncator
  runs, writes back any the truncator removed or moved back, and logs a warning naming the truncator. A truncator that
  leaves them alone causes no write and no warning.
- `IProjectionCheckpointStore` gains an optional member that lists stored checkpoints by consumer prefix, and a data
  record it returns. The default member reports that it is unsupported, so an existing store keeps compiling and
  working: its replay runs as before, with a warning that the positions could not be guarded. Additive; nothing here
  is **BREAKING**.

Unchanged: a projection's record per projection, a missing-prerequisite report for a tenant not recorded, a replay's
emptying of the record, saga checkpoints on a truncator that does not touch them, and a rebuild of one projection.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `projections`: the requirement that a projection can forget a deleted tenant is replaced by six requirements, which
  pass a recorded tenant's facts over before handling, and a new requirement states what a host's truncator must leave
  in place.
- `orleans-execution`: a new requirement that a full replay on a host with store-reading projections keeps the saga
  positions, and how it reports what it had to write back.

## Impact

- **Code:** `src/Stratara.Projections/Services/ProjectionHandler.cs`,
  `src/Stratara.Projections/Abstractions/IForgetsDeletedTenants.cs`,
  `src/Stratara.Abstractions/Abstractions/Projections/IProjectionReplayState.cs`,
  `src/Stratara.Abstractions/Abstractions/Projections/IProjectionCheckpointStore.cs`,
  `src/Stratara.Orleans.EntityFrameworkCore/Projections/ProjectionCheckpointStore.cs`,
  `src/Stratara.Orleans/Projections/ReplayCheckpointReset.cs`, `src/Stratara.Orleans/Projections/ProjectionRebuilder.cs`,
  `src/Stratara.Orleans/Diagnostics/OrleansLog.cs`,
  `src/Stratara.Diagnostics/LogEvents.cs`.
- **Public API:** additive only, in `Stratara.Abstractions`: one default interface member and one record. One
  observable behaviour reversal for declaring projections, described above.
- **Documentation:** `docs/guides/write-a-projection.md`, `docs/guides/tenant-membership.md`,
  `docs/guides/migrate-to-the-orleans-execution-model.md`, `docs/reference/log-events-schema.md`,
  `src/Stratara.Projections/README.md`.
- **Operations:** three new log events in the `117_000s`. More reads of the read store during a replay on hosts with
  declaring projections.
- **Dependencies, schema:** none. No migration.
- **Superseded sources:** the `projections` requirement *A projection can forget a deleted tenant* is dissolved into six
  requirements; its rationale is carried in `design.md`, decision 9. The truncator's previous XML summary is corrected in place.
