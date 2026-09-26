## 1. Reproduce first

- [x] 1.1 `tests/Stratara.Infrastructure.Tests/EventSourcing/SnapshotCommitTests.cs`, against the SQLite
  test store with a strategy that always snapshots: two writers stage on one stream, the one with the
  longer batch saves second and loses; assert no snapshot at its batch's highest version exists and the
  rebuilt aggregate equals the winner's. Confirm it fails on `main`.
- [x] 1.2 Same file: a snapshot service that throws. The save succeeds, the event is stored and its bundle
  published. Confirm it fails on `main`.

## 2. The fix

- [x] 2.1 `EventSource` commits, hands the bundle on, then calls the snapshot service, and logs a failure as
  `LogEvents.EventStore.SnapshotFailed` through an optional `ILogger<EventSource>` — at information only when the
  caller cancelled, as a warning otherwise (`SnapshotCommitTests.A_snapshot_that_times_out_on_its_own_is_logged_as_a_warning`).
- [x] 2.2 `SnapshotService` rebuilds the aggregate up to the batch's highest version and no longer
  applies the batch on top; `SnapshotServiceTests` follow.
- [x] 2.3 `ISnapshotService` documents that it is called after the commit.

- [x] 2.4 `SnapshotService` builds each stream's snapshot on its own: a stream that fails while being built —
  an unreadable earlier snapshot, a type the host does not trust, a timeout the caller did not ask for — does not
  keep the others from being written, and the failures are thrown together afterwards
  (`SnapshotServiceTests.AddSnapshotIfNeeded_AStreamThatFails_DoesNotKeepTheOthersFromBeingWritten`,
  `…AStreamWhoseBuildTimesOut_DoesNotKeepTheOthersFromBeingWritten`). All streams' snapshots are inserted in one
  save, so a failure of the insert itself writes none of them.

## 3. Documentation

- [x] 3.1 `docs/guides/configure-snapshots.md` and `CHANGELOG.md`.

## 4. Verify

- [x] 4.1 `openspec validate write-a-snapshot-only-of-committed-events --strict`.
- [x] 4.2 Local gauntlet green.
