## 1. Reproduce first

- [ ] 1.1 `tests/Stratara.Infrastructure.Tests/EventSourcing/SnapshotOwnerTests.cs`, against the SQLite
  test store with its in-memory key store and a strategy that always snapshots:
  - a stream owned by tenant T and user U whose aggregate has a user-level encrypted property;
  - after the snapshot, erase the key scope of T and U;
  - rebuild and assert the property reads as absent.

  Confirm it fails on `main`, where the snapshot still reads the value.
- [ ] 1.2 Same file: a snapshot triggered by a batch whose first event states another tenant. Assert
  the stored snapshot records the stream owner's tenant. Confirm it fails on `main`.
- [ ] 1.3 Same file: a snapshot row with no user (as written before the change) is still read.

## 2. The fix

- [ ] 2.1 `Snapshot.UserId` (nullable, XML documented) in `src/Stratara.Abstractions/EventSourcing/Snapshot.cs`.
- [ ] 2.2 `SnapshotService` resolves the stream's recorded owner (the batch's version-1 entry, else the
  stream's first entry) and serializes and records under it.
- [ ] 2.3 `AggregationService` reads a snapshot under `snapshot.TenantId` and `snapshot.UserId`.
- [ ] 2.4 Schema tests in `tests/Stratara.EventSourcing.EntityFrameworkCore.Tests` (or wherever the
  snapshot table's shape is pinned) follow the new column. `tests/Stratara.Infrastructure.Tests` green.

## 3. Documentation

- [ ] 3.1 `ISubjectEraser` and `docs/guides/tenant-membership.md`: the snapshot limitation leaves the
  not-covered list.
- [ ] 3.2 `CHANGELOG.md` → *Unreleased*: the fix, the write-context migration and the one-off statement.

## 4. Verify

- [ ] 4.1 `openspec validate keep-a-snapshot-with-its-owning-user --strict`.
- [ ] 4.2 Local gauntlet green.
