# Tasks

## 1. The reader per saga

- [x] 1.1 `SagaGrain` keyed `sagas:<SagaName>/<partition>`: a stateless saga's reader calls `ISagaHandler` for its
      saga; a process's reader forwards to the process grains.
- [x] 1.2 `SagaNudgeTarget` lists, nudges, ensures, pauses and resumes every saga's reader.
- [x] 1.3 `StoreReaderGrain`: an overridable starting position for a consumer without a checkpoint (default 0).
- [x] 1.4 The saga grain's starting position (D2): legacy `sagas` checkpoint, else the highest other saga's, else
      0 — written as the saga's own checkpoint before the first read.
- [x] 1.5 Unit tests (`tests/Stratara.Orleans.Tests`): the starting position in each of the three cases; a
      stateless reader skips entries its saga does not handle.

## 2. The legacy reader

- [x] 2.1 A grain for the legacy key that retires: removes its reminder, logs (new `LogEvents.Orleans` id,
      Information), reads nothing.
- [x] 2.2 `StoreReaderSeeding` and `ExecutionModelReset` (and `InMemoryExecutionModelReset`) name every saga's
      consumer; the reset also removes the legacy `sagas` rows.

## 3. End to end (PostgreSQL, `tests/Stratara.Orleans.IntegrationTests/Sagas`)

- [x] 3.1 Two sagas on one fact, one always throwing: the other applies it once, goes on to later facts, and is
      not run again; the stall is counted under the failing saga's consumer name.
- [x] 3.2 A saga added to a running deployment reacts only to facts after the others' position.
- [x] 3.3 A store with a legacy `sagas` checkpoint: every saga starts there; nothing at or below it is applied.
- [x] 3.4 The existing saga, process and tenant tests pass unchanged.

## 4. Documentation

- [x] 4.1 `docs/guides/operate-the-orleans-execution-model.md`: a failing saga stops only itself; the saga
      checkpoints by name; one deployment per read store for sagas, restated.
- [x] 4.2 `docs/guides/migrate-to-the-orleans-execution-model.md`: the upgrade note (D3) and the rollback note.
- [x] 4.3 `docs/concepts/orleans-execution-model.md` where it describes the saga reader.
- [x] 4.4 `docs/reference/log-events-schema.md`: the new event.
- [x] 4.5 `CHANGELOG.md` `[Unreleased]` → *Fixed* (siblings no longer repeated) and *Changed* (consumer names of
      the saga stalls; the upgrade).
