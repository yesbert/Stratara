## 1. The committed head on the port

- [x] 1.1 Add `CommittedHeadAsync` to `ICommittedPositionReader` with a default that answers `HeadAsync`, documented with when a reader must override it (`src/Stratara.Abstractions/Abstractions/CommitOrder/ICommittedPositionReader.cs`)
- [x] 1.2 Override it in `PostgresTransactionIdReader` with a `CommittedHead` statement without the horizon filter (`src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PostgresTransactionIdReader.cs`)
- [x] 1.3 PostgreSQL test: with a second appending transaction held open below a committed entry, `HeadAsync` lies below the entry and `CommittedHeadAsync` at or above it (`tests/Stratara.Orleans.IntegrationTests/CommitOrder/CommittedHeadTests.cs`)
- [x] 1.4 Portable counter test: `CommittedHeadAsync` equals `HeadAsync` (`tests/Stratara.Testing.Orleans.Tests/CommittedHeadOnThePortableCounterTests.cs`)

## 2. The public abstraction

- [x] 2.1 Add `IProjectionProgress`, `StoreMark`, `ProjectionWaitOutcome`, `ProjectionPartitionLag` and `ProjectionWaitResult` with full XML documentation (`src/Stratara.Abstractions/Abstractions/Projections/IProjectionProgress.cs`)

## 3. The grain side

- [x] 3.1 `StoreReaderLoop`: expose the reader state (advancing, stalled on entry, stalled on read) and signal after every checkpoint it writes (`src/Stratara.Orleans/Projections/StoreReaderLoop.cs`)
- [x] 3.2 `StoreReaderGrain` / `IProjectionGrain`: `[AlwaysInterleave]` `WaitForAsync(position, budget)` that requests a catch-up, registers the waiter, answers on reaching the position or at the budget with `StoreReaderProgress`, and completes its waiters on deactivation (`src/Stratara.Orleans/Projections/StoreReaderGrain.cs`, `ProjectionGrain.cs`)
- [x] 3.3 Tests of the grain's answers: reached once the checkpoint passes the position, and at the budget with the loop's state (stalled, suspended, behind) — through the real grain in the execution-model test host (`tests/Stratara.Testing.Orleans.Tests/ProjectionProgressTests.cs`, 4.3); how the service reads those answers — a partition answered short (budget or deactivation) asked again, a retired grain, the outcome precedence and the budget cap — against hand-written grains (`tests/Stratara.Orleans.Tests/ProjectionProgressTests.cs`). The grain is internal and cannot be activated outside a silo, so the deactivation path itself is covered by the service's re-ask, not by a test that deactivates a grain

## 4. The service

- [x] 4.1 `ProjectionProgress`: `MarkAsync` (reader name + `CommittedHeadAsync` per partition), `WaitAsync` by name and by type, reader-name check (D5), mark `0` counts as reached, parallel grain calls in rounds that answer reached only when every partition does in the same round, capped at two seconds, suspended never counts as reached (D4), outcome precedence `Stalled` > `Suspended` > `TimedOut` (`src/Stratara.Orleans/Projections/ProjectionProgress.cs`)
- [x] 4.2 Register it in `AddStrataraProjectionGrains` beside `IProjectionRebuilder` (`src/Stratara.Orleans/DependencyInjection/OrleansProjectionServiceCollectionExtensions.cs`)
- [x] 4.3 Execution-model test host: *A host waits for its own write*, *The projection does not get there in time*, *A partition is stopped at an entry*, *The projection is suspended*, *The store has nothing in a partition*, *The partition count changed after the mark was taken*, *The projection is unknown*, cancellation, and the type overload (`tests/Stratara.Testing.Orleans.Tests/ProjectionProgressTests.cs`)
- [x] 4.4 PostgreSQL with the native reader: *A host waits for its own write* and *Another write is open when the mark is taken* (`tests/Stratara.Orleans.IntegrationTests/Projections/ProjectionProgressTests.cs`)
- [x] 4.5 PostgreSQL: *The projection is rebuilt during the wait* — a wait started before a rebuild answers `Reached` only once the rebuilt read model holds the mark's entries (`tests/Stratara.Orleans.IntegrationTests/Projections/ProjectionProgressTests.cs`)

## 5. Documentation and release notes

- [x] 5.1 `docs/guides/write-a-projection.md`: waiting for one's own write — take the mark after the commit, wait, branch on the answer
- [x] 5.2 `docs/guides/operate-the-orleans-execution-model.md`: what each answer means, the long-running-transaction cause of a timeout, the cost of a mark, adopt once every silo runs the release
- [x] 5.3 `docs/concepts/orleans-execution-model.md` and `llms.txt`: one paragraph on the mark and the wait
- [x] 5.4 `CHANGELOG.md`: entry under the unreleased patch

## 6. Verification

- [x] 6.1 `openspec validate let-a-host-wait-for-a-projection --strict`
- [x] 6.2 `./scripts/local-gauntlet.sh` green
- [x] 6.3 `dotnet test tests/Stratara.Orleans.IntegrationTests` green for the new tests (Docker)
