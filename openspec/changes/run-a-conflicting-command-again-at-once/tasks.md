## 1. Red first

- [x] 1.1 *A command's first attempt meets a conflict*: a `RecordedIntentHost` with `IntentGrace` 30 s and a probe
  whose handler throws `ConcurrencyException` on its first run and succeeds on its second, recording a scoped marker's
  identity per run. Assert that it succeeds within a second of the conflict, that the two runs saw different markers,
  that the record holds `(0, 1)` attempts and conflicts and is completed, and that it ran twice. Run it against the
  unchanged code and record that it fails (`tests/Stratara.Orleans.IntegrationTests/Aggregates/ConflictRerunTests.cs`).
  Result on the unchanged code: the command ran once within 20 s and waited for the grace. The record is removed on
  completion, so the test asserts its removal instead of its counts

## 2. Store

- [x] 2.1 `ICommandIntentStore.CountConflictAsync(Guid, string, CancellationToken)`, a default member that calls
  `RecordConflictAsync` and answers `null`, with XML documentation
  (`src/Stratara.Abstractions/Abstractions/Outbox/ICommandIntentStore.cs`)
- [x] 2.2 `CommandIntentStore.CountConflictAsync`: the update `RecordConflictAsync` makes, then a read of
  `ConflictCount` (`src/Stratara.Orleans.EntityFrameworkCore/Intents/CommandIntentStore.cs`)
- [x] 2.3 Unit test: the default records through `RecordConflictAsync` and answers `null`
  (`tests/Stratara.Orleans.Tests/IntentStoreDefaultsTests.cs`)

## 3. Rerun

- [x] 3.1 `IntentLease.CountConflictAsync(ConcurrencyException, commandType, aggregateId)`: logs the attempt as
  `RecordFailureAsync` does, records through `CountConflictAsync`, answers the count or `null` on a failure to record
  (`src/Stratara.Orleans/Aggregates/IntentLease.cs`)
- [x] 3.2 `CommandExecution.RunIntentAsync` runs the attempts through `ResilienceNames.ConcurrencyConflict` inside
  `around`. Every attempt after the first gets a scope of its own. Non-conflict exceptions pass through the pipeline
  wrapped and are unwrapped outside. A conflict reruns only while `count <= MaxConflictRequeues`, and the recorded last
  conflict is not recorded twice (`src/Stratara.Orleans/Aggregates/AggregateGrain.cs`, design D1/D2)
- [x] 3.3 Test 1.1 passes, and `ResumedOnceInOrderTests` still asserts `MaxConflictRequeues + 1` runs
- [x] 3.4 *A heavy command's first attempt meets a conflict*, at `CommandExecution.RunIntentAsync` with an `around`
  that counts its entries: one entry, two attempts, the second in another scope
  (`tests/Stratara.Orleans.Tests/ConflictRerunTests.cs`)
- [x] 3.5 *The store cannot count conflicts*: with a store that only implements `RecordConflictAsync`, a conflict runs
  the handler once and is recorded (`tests/Stratara.Orleans.Tests/ConflictRerunTests.cs`)

## 4. Documentation and release notes

- [x] 4.1 `docs/guides/operate-the-orleans-execution-model.md`, *Kept commands*: how a conflict is handled, the
  pipeline as the knob, the bound, and that a provider's own conflict exception is an ordinary failure here
- [x] 4.2 `CHANGELOG.md` `[Unreleased]`: Changed (a conflict reruns at once) and Added (`CountConflictAsync`)
- [x] 4.3 `openspec validate run-a-conflicting-command-again-at-once --strict`, `./scripts/local-gauntlet.sh`, and the
  integration classes `ConflictRerunTests` and `ResumedOnceInOrderTests`
  Result: gauntlet green; `ConflictRerunTests` and `ResumedOnceInOrderTests` green (23 of 23). The namespaces
  `Aggregates` and `HeavyWork` passed 68 of 69 under heavy unrelated machine load. The one failure,
  `HeavyBurstTests` running one unit twice after hand-overs that timed out, involves no conflict: its probe appends
  nothing
