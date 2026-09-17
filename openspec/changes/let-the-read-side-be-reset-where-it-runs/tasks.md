# Tasks

## 1. The reset

- [x] 1.1 `IProjectionCheckpointStore.ResetAsync` with a default implementation over `SetAsync`.
- [x] 1.2 The framework's store implements it: the beginning, under the resetting reader's name,
      whatever reader held the row; inserted where there is none.
- [x] 1.3 `ProjectionRebuilder` and the replay's checkpoint reset use it.
- [x] 1.4 Unit tests: a reset takes a row held under another reader and another partition count, and
      reading and advancing are still refused.

## 2. The pause

- [x] 2.1 `StoreReaderPause` pauses every reader, resumes what it paused where one fails, and throws
      naming the partitions it could not pause.
- [x] 2.2 `ProjectionRebuilder` and the replay's reset use it and resume only what they hold.
- [x] 2.3 Unit tests: a failing pause leaves no reader paused, and the rebuild reports it.

## 3. The dispatcher

- [x] 3.1 Decide hybrid before the guard in `AddStoreReaderCore`; keep the dispatcher or fail naming
      what is missing on every registration that asks.
- [x] 3.2 Register a type-shaped kept dispatcher by its type, so the container disposes it.
- [x] 3.3 Unit tests in `HybridBundleDispatcherTests`: a second registration that asks for hybrid,
      with and without a bus dispatcher.

## 4. The wake-up

- [x] 4.1 Log event `117_121`, written where a nudge fails, with the consumer and partition.
- [x] 4.2 The nudge target wakes the consumers behind one that failed.
- [x] 4.3 Unit test: a failing nudge is logged and the others are still woken.

## 5. Documentation and the run

- [x] 5.1 Operate guide: the rebuild and the replay are how a refused checkpoint is recovered inside a
      running cluster.
- [x] 5.2 `docs/reference/log-events-schema.md` and `CHANGELOG.md`.
- [x] 5.3 Gauntlet and the projection integration suites.
