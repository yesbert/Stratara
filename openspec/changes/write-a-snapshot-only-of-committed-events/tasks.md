## 1. Reproduce first

- [ ] 1.1 `tests/Stratara.Infrastructure.Tests/EventSourcing/SnapshotCommitTests.cs`, against the SQLite
  test store with a strategy that always snapshots: two writers stage on one stream, the one with the
  longer batch saves second and loses; assert no snapshot at its batch's highest version exists and the
  rebuilt aggregate equals the winner's. Confirm it fails on `main`.
- [ ] 1.2 Same file: a snapshot service that throws. The save succeeds, the event is stored and its bundle
  published. Confirm it fails on `main`.

## 2. The fix

- [ ] 2.1 `EventSource` calls the snapshot service after the commit, logs a failure as
  `LogEvents.EventStore.SnapshotFailed` through an optional `ILogger<EventSource>`, and hands the bundle
  on afterwards.
- [ ] 2.2 `SnapshotService` rebuilds the aggregate up to the batch's highest version and no longer
  applies the batch on top; `SnapshotServiceTests` follow.
- [ ] 2.3 `ISnapshotService` documents that it is called after the commit.

## 3. Documentation

- [ ] 3.1 `docs/guides/configure-snapshots.md` and `CHANGELOG.md`.

## 4. Verify

- [ ] 4.1 `openspec validate write-a-snapshot-only-of-committed-events --strict`.
- [ ] 4.2 Local gauntlet green.
