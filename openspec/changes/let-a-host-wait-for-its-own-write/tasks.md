## 1. The mark of streams

- [x] 1.1 `IProjectionProgress.MarkAsync(IEnumerable<Guid> streamIds, CancellationToken)` with a default that answers `MarkAsync()`, documented with when to take which (`src/Stratara.Abstractions/Abstractions/Projections/IProjectionProgress.cs`)
- [x] 1.2 `ProjectionProgress`: the committed head of the streams' partitions, 0 elsewhere (`src/Stratara.Orleans/Projections/ProjectionProgress.cs`)

## 2. Tests

- [x] 2.1 Execution-model test host (portable reader): *A host waits for its own write while another partition lags*, *A mark of streams names a stream with nothing committed*, *A mark of the whole store still waits for every partition*, the first and third in one test (`tests/Stratara.Testing.Orleans.Tests/ProjectionProgressTests.cs`, `A_host_waits_for_its_own_write_while_another_partition_is_stopped`, `A_mark_of_a_stream_nothing_was_committed_to_is_reached_at_once`)
- [x] 2.2 PostgreSQL with the native reader: *A host waits for its own write while another partition lags* (`tests/Stratara.Orleans.IntegrationTests/Projections/ProjectionProgressTests.cs`)

## 3. Documentation and release notes

- [x] 3.1 `docs/guides/write-a-projection.md`, *Read your own write*: the example takes the stream mark, and the global mark is named for decisions across streams. `docs/guides/operate-the-orleans-execution-model.md` (the cost per constrained partition) and `llms.txt`
- [x] 3.2 `CHANGELOG.md`: entry under `[Unreleased]`

## 4. Verification

- [x] 4.1 `openspec validate let-a-host-wait-for-its-own-write --strict`
- [x] 4.2 `./scripts/local-gauntlet.sh` green
- [x] 4.3 `dotnet test tests/Stratara.Orleans.IntegrationTests` green for the new tests (Docker)
