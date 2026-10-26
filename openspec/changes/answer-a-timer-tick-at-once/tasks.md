## 1. Red first

- [ ] 1.1 `LongHandlerTimerTests` captures the silo's log and asserts, besides one start, one completion and the
  timer unregistered, that the log has no `Could not deliver reminder tick` and no `Response did not arrive on time`
  for the owner. Run it against the current code and record that it fails on the log assertion
  (`tests/Stratara.Orleans.IntegrationTests/Timers/LongHandlerTimerTests.cs`)
- [ ] 1.2 *A timer's handler fails*: a handler that throws on its first attempt and returns on its second. The test
  asserts that `117_137` is logged once with the owner and the purpose, that the second attempt runs a retry period
  later, and that the timer is unregistered after it. Run it against the current code and record that it fails
  because the event is missing (`tests/Stratara.Orleans.IntegrationTests/Timers/FailingHandlerTimerTests.cs`)

## 2. The tick hands the handler over

- [ ] 2.1 `ITimerOwnerGrain.FireDueAsync(string reminderName)`, `[OneWay]`, with its own alias, documented as only
  ever called by the activation on itself (design D1, D4) (`src/Stratara.Orleans/Timers/ITimerOwnerGrain.cs`)
- [ ] 2.2 `ReceiveReminder` sends `FireDueAsync` through `this.AsReference<ITimerOwnerGrain>()` and returns, with a
  continuation attached only on fault that does nothing beyond observing the failure (design D1)
  (`src/Stratara.Orleans/Timers/TimerOwnerGrain.cs`)
- [ ] 2.3 `FireDueAsync` owns `_firing`, `_ticks` and `_renewed` as `ReceiveReminder` did. It logs `117_008` on a
  stop and `117_137` on any other failure, and does not rethrow (design D2, D3). Update the class's XML summary
  (`TimerOwnerGrain.cs`)
- [ ] 2.4 `LogEvents.Orleans.TimerHandlerFailed = 117_137` and `OrleansLog.LogTimerHandlerFailed(exception, owner,
  purpose)` at Error (`src/Stratara.Diagnostics/LogEvents.cs`, `src/Stratara.Orleans/Diagnostics/OrleansLog.cs`)

## 3. Green

- [ ] 3.1 1.1 and 1.2 pass
- [ ] 3.2 The timer integration classes stay green: `Timers/OwnerCheckedTimerTests`, `Timers/HardKillTimerTests`,
  `Timers/ReRegisteredTimerTests`, `Timers/CommittedTimerTests`, `Sagas/TimerReentrancyTests`,
  `Sagas/SagaProcessTimeoutTests`, and `Hosting/StoppingSiloTests` (a handler stopped with its silo)
- [ ] 3.3 `tests/Stratara.Orleans.Tests` green. A grain-surface test that lists `ITimerOwnerGrain`'s methods or
  aliases is updated for the new method

## 4. Documentation and release notes

- [ ] 4.1 `docs/guides/operate-the-orleans-execution-model.md`, *Reminder profile and clocks*: a durable timer
  answers its tick while its handler runs, and a long handler no longer leaves `Could not deliver reminder tick`.
  A failing handler logs `117_137` and fires again after the retry period. Keep the advice to hand long work to a
  heavy command, now as a matter of throughput rather than of the log
- [ ] 4.2 Same guide, *What to watch*: drop "a durable timer's long handler leaves the line as a matter of course",
  and add `117_137` to the events to route to an alert
- [ ] 4.3 `docs/reference/log-events-schema.md`: add the `117_137` row
- [ ] 4.4 Check `docs/concepts/orleans-execution-model.md`, the other guides and `llms.txt` for a claim about timer
  handlers and reminder lines that this change contradicts
- [ ] 4.5 `CHANGELOG.md` under `[Unreleased]`, *Fixed*

## 5. Verification

- [ ] 5.1 `openspec validate answer-a-timer-tick-at-once --strict`
- [ ] 5.2 `./scripts/local-gauntlet.sh` green
- [ ] 5.3 Sonar on the branch before any tag (`gh workflow run sonar.yml --ref <branch>`)
