## 1. Red first

- [x] 1.1 *A projection catches up longer than the response timeout*: a silo on the PostgreSQL store with
  `SiloMessagingOptions.ResponseTimeout` at two seconds, a keep-alive period of one or two seconds (lower
  `ReminderOptions.MinimumReminderPeriod` through `ExecutionModelTestHostOptions`), and a projection whose
  apply is slowed so that its backlog takes at least three keep-alive periods. The silo's log is captured.
  The test asserts that the checkpoint reaches the end and that the log has no `Could not deliver reminder tick`
  and no `Response did not arrive on time`. Run it against the unchanged code and record that it fails on the
  log assertion (`tests/Stratara.Orleans.IntegrationTests/Projections/BusyReaderKeepAliveTests.cs`)
- [x] 1.2 *A saga reader catches up longer than the response timeout*: the same set-up for a stateless saga.
  Every entry is handled once, in commit order (`BusyReaderKeepAliveTests.cs`)
- [x] 1.3 *A singleton work runs longer than the response timeout*: a work whose run lasts several keep-alive
  periods. The test asserts that no two runs overlap and that the log has no `Could not deliver reminder tick`
  (`tests/Stratara.Orleans.IntegrationTests/Singleton/BusySingletonKeepAliveTests.cs`)
- [x] 1.4 *A silo starts while a singleton work runs long*: a second silo registering the same work is started
  during a long run. The start succeeds, and the run is neither interrupted nor run twice. Record whether it
  fails on the unchanged code. If it does not, keep it as a guard and note the result here and in design D3
  (`BusySingletonKeepAliveTests.cs`). Result: it failed. The second silo's start threw `TimeoutException` on
  `ISingletonWorkGrain.EnsureRunningAsync()`

## 2. Store readers answer at once

- [x] 2.1 `IRemindable.ReceiveReminder` and the poll timer callback call `PollAsync().Ignore()` and return a
  completed task. The timer stays `Interleave = false, KeepAlive = true`. Update the XML summary of the class
  and of `PollAsync` (`src/Stratara.Orleans/Projections/StoreReaderGrain.cs`, design D1)
- [x] 2.2 One shared static predicate that matches an `IInvokable` whose interface type is `IRemindable`, used
  by `[MayInterleave(...)]` on `ProjectionGrain`, `SagaReaderGrain` and the retired `SagaGrain` (design D2)
  (`src/Stratara.Orleans/Hosting/KeepAliveInterleaving.cs`)
- [x] 2.3 `[AlwaysInterleave]` on `EnsureRunningAsync` of `IProjectionGrain` and `ISagaReaderGrain`, with aliases
  unchanged. `ISagaGrain` stays as it is (`ProjectionGrain.cs`, `SagaReaderGrain.cs`)
- [x] 2.4 Unit test of the predicate: true for the reminder's invokable, false for the grains' own interfaces.
  Each grain declares a public static predicate on its own class, and each start call interleaves
  (`tests/Stratara.Orleans.Tests/KeepAliveInterleavingTests.cs`)

## 3. Singleton work answers at once

- [x] 3.1 `[MayInterleave(...)]` with the shared predicate on `SingletonWorkGrain`, and `[AlwaysInterleave]`
  on `ISingletonWorkGrain.EnsureRunningAsync`. The run timer stays non-interleaving. Update the class's XML
  summary to say that the keep-alive and the start call interleave and runs still do not
  (`src/Stratara.Orleans/Singleton/SingletonWorkGrain.cs`, design D2)

## 4. Green

- [x] 4.1 1.1 to 1.4 pass
- [x] 4.2 *A keep-alive during a catch-up reads once more*: a fact committed during a long catch-up is applied
  by that catch-up, and no second loop runs beside it. Covered by both tests of `BusyReaderKeepAliveTests`: the
  stream committed into the held partition is applied once, and at most one entry is ever applied at a time
- [x] 4.3 The touched integration classes stay green: `Projections/ProjectionGrainTests`,
  `Projections/PausedReaderLeaseTests`, `Projections/RetiredReaderTests`, `Projections/ProjectionProgressTests`,
  `Sagas/SagaIsolationTests`, `Sagas/SagaGrainTests`, `Singleton/SingletonWorkTests`, `Singleton/SingletonPlacementTests`

## 5. Documentation and release notes

- [x] 5.1 `docs/guides/operate-the-orleans-execution-model.md`, *Reminder profile and clocks*: say that store
  readers and singleton work answer their keep-alive while they read or run, and that a durable timer whose
  handler outlasts the reminder call's response timeout makes the runtime log `Could not deliver reminder tick`
  at Error for that tick, and that this says nothing about the timer
- [x] 5.2 Same guide, *What to watch*: `Could not deliver reminder tick` for a store reader or a singleton work
  is worth an alert now
- [x] 5.3 Check `docs/concepts/orleans-execution-model.md`, the other guides and `llms.txt` for a claim about
  keep-alives that this change contradicts
- [x] 5.4 `CHANGELOG.md`: entry under `[Unreleased]`. The CHANGELOG names no consumer findings, so F-028 is
  named in the proposal and the pull request instead

## 5a. Review findings (`/code-review high`; Copilot's quota was exhausted)

- [x] 5a.1 A poll or keep-alive no longer re-dirties a running loop, so a loop stalled on an entry ends after each
  attempt as before (design D1) (`StoreReaderGrain.RequestPoll`)
- [x] 5a.2 The predicate is renamed `IsReminderTick`, and its documentation says it matches every reminder
- [x] 5a.3 `KeepAliveInterleavingTests` finds every concrete store reader by reflection. `SagaGrain` carries the
  predicate too
- [x] 5a.4 The silo-start test asserts that the long run ended and was not cancelled, and holds it for 30 s so
  that a slow start still overlaps it. The assertion that could never fail is removed
- [x] 5a.5 The operating guide no longer says the reminder line can only mean an unreachable grain. A deactivation
  waiting for a run can still leave one, and the spec allows it
- [x] 5a.6 Design D2's wire-compatibility note is corrected: `[AlwaysInterleave]` travels with the caller's
  message
- [x] 5a.7 Answered without a change: a pause lapsing during `PauseAsync`'s wait was already possible and is
  handled, and overlapping start calls upsert the same reminder (design, *Risks*)

## 6. Verification

- [x] 6.1 `openspec validate answer-a-keep-alive-while-busy --strict`
- [x] 6.2 `./scripts/local-gauntlet.sh` green
- [x] 6.3 The integration classes of 1.x and 4.3 green locally (Docker). The full suite runs through
  `integration.yml` after the merge
