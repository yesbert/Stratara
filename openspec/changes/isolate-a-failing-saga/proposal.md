# isolate-a-failing-saga

> **Status:** proposed

## Why

On the Orleans execution model every store-reading saga of a deployment shares one reader per
partition and one checkpoint (`sagas`). An entry is applied to all of them at once, and a failure of
any one stops the partition at that entry and retries the whole entry on every poll and wake-up —
without a bound. The pre-4.2.0 review found what that means for a consumer:

- **Sagas that succeeded run again, every five seconds, until the failing one is fixed.** A
  `SendConfirmationEmailSaga` beside a broken `BillingSaga` on the same event sends the email on every
  retry. On the bus a sibling is repeated a bounded number of times before the bundle is dead-lettered;
  on Orleans it is repeated for as long as the failure lasts.
- **One failing saga stops every other saga in that partition.** Every later fact of the partition
  waits for it, including facts no failing saga reacts to.

The owner chose to isolate each saga rather than track which sagas of an entry succeeded
(2026-09-18): a saga reads the store with a checkpoint of its own, as a projection already does.

## What Changes

- **Each store-reading saga reads with its own checkpoint, per partition.** A stateless saga's
  reader hands the entry to that saga alone; a stateful process's reader hands it to that process's
  grains. A saga that fails stops only its own reading; the others advance past the entry and never
  run it again because of that failure. The failure stays visible exactly as today: logged with the
  entry and counted as a stall, now under the saga's consumer name.
- **A saga without a checkpoint of its own starts where the host's sagas already read** — at the
  shared checkpoint a 4.1.x deployment left behind, or else at the furthest checkpoint another saga of
  the host holds in that partition — and at the beginning of the store only where no saga has read.
  A saga added to a running deployment therefore does not replay the store's history into its side
  effects, as it did not when the sagas shared a reader.
- **Seeding, the execution-model reset and the wake-up name every saga's consumer**, and the reset
  also removes the shared checkpoint a 4.1.x deployment left.
- **Upgrade.** A saga-role silo on 4.2.0 retires a shared saga reader that is brought back on it,
  so the old reader's keep-alive does not keep it running beside the new ones. During a rolling
  upgrade a fact may reach a saga twice — once through an old silo's shared reader, once through the
  new saga's reader — which is the at-least-once delivery sagas already tolerate; the upgrade note
  recommends upgrading the saga silos together.
- **What does not change:** sagas still run in parallel with each other and in order within
  themselves; a failing saga is still retried without a bound on Orleans (a bounded retry with a kept
  state would need storage and was not chosen); at most one deployment sharing a read store runs
  store-reading sagas.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`:
  - *A failing entry stops its partition, is retried, and is visible* — for sagas, the failing saga's
    reading stops, not the partition's; siblings are not repeated.
  - *A host can start its store readers at the head of a populated store* — a saga registered later
    starts where the host's sagas read, not at the beginning.
  - *A read store's checkpoints belong to the consumers that read into it* — each store-reading saga
    is a consumer of its own; the one-deployment rule for sagas is restated for the set.

## Impact

- Affected specs: `orleans-execution`
- Affected code: `src/Stratara.Orleans/Sagas/SagaGrain.cs` (per-saga reader, the retiring shared
  reader, `SagaNudgeTarget`), `src/Stratara.Orleans/Projections/StoreReaderGrain.cs` (the starting
  position a subclass may supply), `src/Stratara.Orleans/DependencyInjection/*Saga*` registration,
  `src/Stratara.Orleans/Projections/StoreReaderSeeding.cs`,
  `src/Stratara.Orleans.EntityFrameworkCore/Hosting/ExecutionModelReset.cs`,
  `src/Stratara.Testing.Orleans/InMemoryExecutionModelReset.cs`, `src/Stratara.Diagnostics/LogEvents.cs`
  (a retired shared saga reader)
- Affected docs: `docs/guides/operate-the-orleans-execution-model.md` (a failing saga; the saga
  checkpoints; the upgrade), `docs/guides/migrate-to-the-orleans-execution-model.md` (upgrade note),
  `docs/concepts/orleans-execution-model.md` where it describes the saga reader, `CHANGELOG.md`
- Superseded: `openspec/changes/archive/2026-09-16-close-the-orleans-readiness-gaps/proposal.md`,
  "Not in scope" — "saga retries repeating stateless sagas".
- Public API: none. Metric and log names unchanged; the stall's consumer tag now carries the saga's
  consumer name instead of `sagas`, which a dashboard filtering on `sagas` must widen — stated in the
  CHANGELOG.
- Schema: none. The checkpoint table keeps one row per saga and partition.
- Versioning: part of 4.2.0.
