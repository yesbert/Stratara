## 1. Red first

- [x] 1.1 `LongHandlerTimerTests` captures the silo's log and asserts, besides one start, one completion and the
  timer unregistered, that the log has no `Could not deliver reminder tick` and no `Response did not arrive on time`
  for the owner. Run it against the current code and record that it fails on the log assertion
  (`tests/Stratara.Orleans.IntegrationTests/Timers/LongHandlerTimerTests.cs`). Result: it failed. The log held
  `Response did not arrive on time in '00:00:02'` and `Could not deliver reminder tick` for the owner
- [x] 1.2 *A timer's handler fails*: a handler that throws on its first attempt and returns on its second. The test
  asserts that `117_137` is logged once with the owner and the purpose, that the second attempt runs a retry period
  later, and that the timer is unregistered after it. Run it against the current code and record that it fails
  because the event is missing (`tests/Stratara.Orleans.IntegrationTests/Timers/FailingHandlerTimerTests.cs`).
  Result: it failed on the missing event. The two attempts and the unregistration already held

## 2. The tick hands the handler over

- [x] 2.1 `ITimerOwnerGrain.FireDueAsync(string reminderName)`, `[OneWay]`, with its own alias, documented as only
  ever called by the activation on itself (design D1, D4) (`src/Stratara.Orleans/Timers/ITimerOwnerGrain.cs`)
- [x] 2.2 `ReceiveReminder` sends `FireDueAsync` through `this.AsReference<ITimerOwnerGrain>()` and returns. `Ignore()`
  observes a send that fails, and nothing else is needed, because the reminder stays registered (design D1)
  (`src/Stratara.Orleans/Timers/TimerOwnerGrain.cs`)
- [x] 2.3 `FireDueAsync` owns `_firing`, `_ticks` and `_renewed` as `ReceiveReminder` did. It logs `117_008` on a
  stop and `117_137` on any other failure, and does not rethrow (design D2, D3). Update the class's XML summary
  (`TimerOwnerGrain.cs`)
- [x] 2.4 `LogEvents.Orleans.TimerFiringFailed = 117_137` and `OrleansLog.LogTimerFiringFailed(exception, owner,
  purpose)` at Error (`src/Stratara.Diagnostics/LogEvents.cs`, `src/Stratara.Orleans/Diagnostics/OrleansLog.cs`)

## 3. Green

- [x] 3.1 1.1 and 1.2 pass
- [x] 3.2 The timer integration classes stay green: `Timers/OwnerCheckedTimerTests`, `Timers/HardKillTimerTests`,
  `Timers/ReRegisteredTimerTests`, `Timers/CommittedTimerTests`, `Sagas/TimerReentrancyTests`,
  `Sagas/SagaProcessTimeoutTests`, and `Hosting/StoppingSiloTests` (a handler stopped with its silo)
- [x] 3.3 `tests/Stratara.Orleans.Tests` green (362). No unit test lists `ITimerOwnerGrain`'s methods, so nothing
  needed updating

## 4. Documentation and release notes

- [x] 4.1 `docs/guides/operate-the-orleans-execution-model.md`, *Reminder profile and clocks*: a durable timer
  answers its tick while its handler runs, and a long handler no longer leaves `Could not deliver reminder tick`.
  A failing handler logs `117_137` and fires again after the retry period. Keep the advice to hand long work to a
  heavy command, now as a matter of throughput rather than of the log
- [x] 4.2 Same guide, *What to watch*: drop "a durable timer's long handler leaves the line as a matter of course",
  and add `117_137` to the events to route to an alert
- [x] 4.3 `docs/reference/log-events-schema.md`: add the `117_137` row
- [x] 4.4 Check `docs/concepts/orleans-execution-model.md`, the other guides and `llms.txt` for a claim about timer
  handlers and reminder lines that this change contradicts
- [x] 4.5 `CHANGELOG.md` under `[Unreleased]`, *Fixed*

## 4a. Review findings (`/code-review high`; Copilot's quota was exhausted)

- [x] 4a.1 The event is `TimerFiringFailed`, not `TimerHandlerFailed`. The catch also sees failures of the owner check,
  port resolution, reminder lookup and unregistration around the handler, and the guide, the log reference and the
  spec say so (design D3)
- [x] 4a.2 The purpose for both log lines comes from `ReminderName.PurposeOf`, which never throws, so a reminder name that
  cannot be decoded no longer escapes the catch unlogged (`TimerOwnerGrain.cs`)
- [x] 4a.3 `ReceiveReminder` sends nothing while the timer's handler runs (design D1)
- [x] 4a.4 Design D1 describes `.Ignore()` as built, and D4 says that a forwarded self-call can reach an earlier version
  and lose that tick, recovered by the next one
- [x] 4a.5 Answered without a change: the log assertion's filter on the owner matched both lines in the red run, so it
  is not vacuous; the duplicated capturing logger providers are a test-wide refactor of their own

## 5. Verification

- [x] 5.1 `openspec validate answer-a-timer-tick-at-once --strict`
- [x] 5.2 `./scripts/local-gauntlet.sh` green
- [ ] 5.3 Sonar on `main` once this and `name-the-projections-once` are merged, before the tag
  (`gh workflow run sonar.yml --ref main`)
