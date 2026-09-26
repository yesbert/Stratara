# Keep a snapshot with its owning user

> **Status:** approved

## Why

A snapshot holds an aggregate's whole state, protected fields included. It is encrypted under the
stream's tenant alone, and the stored snapshot records no user. For an aggregate that belongs to a user,
the user's erasure therefore does not reach its snapshots. Rebuilding the aggregate after the erasure
starts from a snapshot that still reads the user's data, even though every event the snapshot was built
from has become unreadable. `keep-the-owning-user-with-the-stream` made every event of such a stream
carry its user. The snapshot is the last place where the user drops out.

A second defect has the same cause. The snapshot takes its tenant from the first entry *of the batch*
that triggered it, not from the stream's recorded owner. When that entry was appended on behalf of
another subject, the snapshot is written under that subject. Its tenant's erasure then reaches the
snapshot, and the stream owner's erasure does not.

## What Changes

The consumer-visible effect: a snapshot is protected under the stream's recorded owner, its tenant and
its user where one was recorded. So a user's erasure reaches the snapshots of that user's aggregates as
it reaches their events.

- A snapshot records the owning user alongside the owning tenant. It is encrypted under both, and read
  back under what it records.
- The owner is the stream's recorded owner, taken from the stream's first event, never from a
  stated subject of the batch that triggered the snapshot.
- A snapshot written before this change records no user. It is still read under its tenant alone, so
  existing snapshots keep working.
- **Migration:** the snapshot table gains a nullable user column. A host generates an EF Core
  migration for its write context. The changelog gives a one-off statement that removes the snapshots
  of user-owned streams written before the upgrade. They are a cache, rewritten at the next threshold,
  and removing them lets a user's erasure reach every copy of that user's aggregate.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `aggregate-rehydration`: *A snapshot captures state per stream and aggregate type* says a snapshot
  is protected under the stream's recorded owner, tenant and user. It covers a snapshot written before
  the user was recorded, and one triggered by a batch whose first event states another subject.

## Impact

- `src/Stratara.Abstractions/EventSourcing/Snapshot.cs` — a nullable `UserId`.
- `src/Stratara.Infrastructure/EventSourcing/SnapshotService.cs` — writes under the stream's recorded
  owner and records the user.
- `src/Stratara.Infrastructure/EventSourcing/AggregationService.cs` — reads a snapshot under the tenant
  and user it records.
- `src/Stratara.EventSourcing.EntityFrameworkCore` — the write context's snapshot table gains the
  column; schema tests follow.
- `src/Stratara.Abstractions/Abstractions/Erasure/ISubjectEraser.cs` and
  `docs/guides/tenant-membership.md` — the snapshot limitation leaves the not-covered list.
- `CHANGELOG.md` — entry and *Upgrading* notes: the migration, and the one-off removal of pre-upgrade
  snapshots of user-owned streams.
