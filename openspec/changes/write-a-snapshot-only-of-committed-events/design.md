## Context

`EventSource.SaveChangesAsync` adds the entries to its unit of work and calls
`ISnapshotService.AddSnapshotIfNeededAsync` before committing. `SnapshotService` opens its own unit of
work — a fresh context — rebuilds the aggregate from the committed stream, applies the batch's events
on top, and commits the snapshot. Only then do the events commit. Evidence: the implementation, and the
reviews of #165 and #167, which both found it.

## Goals / Non-Goals

**Goals:**
- A snapshot never captures an event that was not committed.
- A snapshot, being a cache, never fails a save whose events are committed.

**Non-Goals:**
- Committing the snapshot in the events' own transaction. That needs a new member on
  `ISnapshotService` to pass the transaction. Writing it after the commit reaches the same guarantee
  without changing the interface.

## Decisions

**Snapshot after the commit, from the committed stream.** After the commit the snapshot service
rebuilds the aggregate up to the batch's highest version — the batch is committed by then — and
stores it. It no longer applies the batch on top of an earlier rebuild, which after the commit would
apply those events twice.
- *Alternative:* one transaction for events and snapshot. Rejected for now, see Non-Goals.

**A snapshot failure after the commit is logged, not thrown.** The events are recorded; throwing would
tell the caller the save failed and invite a retry that records them twice. The next threshold writes
a snapshot, and a rebuild without one replays the events.

**The snapshot runs after the bundle is handed on.** Publication is what readers wait for; a snapshot is
a cache. Taking it last means a slow or failing snapshot never delays or blocks publication, and a
cancellation that arrives during it — after the events are committed and published — is logged, not
thrown.

**Its dependencies shrink.** Building from the committed stream needs neither the event mapper nor the
aggregate event selector, so `SnapshotService` no longer takes them.

## Risks / Trade-offs

- [Risk] Another writer commits between this save's commit and its snapshot. → The snapshot is bounded
  to this batch's highest version, so a later event is not in it; a snapshot at that version by the
  other writer cannot exist, because the version is this save's.
- [Risk] The snapshot now reads the stream once more after the commit. → It already rebuilt the
  aggregate before; the work is the same.
