## Context

`SnapshotService` builds a snapshot inside `SaveChangesAsync`, from the batch's entries for one stream,
and serializes it under `streamEntries[0].TenantId`. `AggregationService` reads it back under
`snapshot.TenantId`. `Snapshot` has no user. Evidence: the implementation, and the review of #161,
which traced it (`SnapshotService.cs`, `AggregationService.cs`).

## Goals / Non-Goals

**Goals:**
- A snapshot is encrypted under the same owner as its stream's events, so every erasure that reaches the
  events reaches the snapshot.
- Existing snapshots stay readable.

**Non-Goals:**
- Rewriting or re-encrypting existing snapshots. They are a cache; the changelog tells a host how to
  drop the ones that matter.

## Decisions

**Record the user on the snapshot, as a nullable column.** The read must know the scope the snapshot
was written under, and that differs between an old snapshot (tenant only) and a new one (tenant and
user).
- *Alternative:* derive the user at read time from the stream's first event. Rejected. An old snapshot
  would then be read under a scope it was not written under, and its protected fields would silently
  read as absent (`data-encryption` → *Unreadable encrypted fields degrade rather than fail*). The
  rebuilt aggregate would be corrupt without anyone noticing.

**Take the owner from the stream's first event.** Use the batch's own version-1 entry when the batch
creates the stream (it is not committed yet), and otherwise read the stream's first entry in the same
transaction that decides whether to snapshot.
- *Alternative:* keep taking the batch's first entry. Rejected, because with a stated subject on that
  entry the snapshot lands under the wrong owner.

## Risks / Trade-offs

- [Risk] A host that skips the migration fails on every snapshot read, because the repository selects
  the new column, and on every snapshot write. → The changelog marks the migration as required before
  the new version runs, and the release's *Upgrading* list carries it.
- [Risk] Pre-upgrade snapshots of user-owned streams stay under the tenant, and since snapshots are
  never pruned they keep serving rebuilds bounded to their version. → The changelog gives the one-off
  statement that removes them; a rebuild without one replays the events.

## Migration Plan

Generate an EF Core migration for the write context (one nullable column). Optionally run the one-off
statement from the changelog. Rolling back drops the column; snapshots written in between would be read
under the tenant alone and their user-level fields would read as absent. Delete them on rollback too.
