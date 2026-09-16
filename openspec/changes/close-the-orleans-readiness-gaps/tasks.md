## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.
      Done: approved by the owner on 2026-09-16, recorded at the owner's request.

## 1. Recorded commands have a kind of their own (D1)

- [x] 1.1 The intent store records commands under the execution model's own kind, and its reads, claim,
      keep and completion accept the new kind and the 4.1.0 kind. Verify:
      `src/Stratara.Orleans.EntityFrameworkCore/Intents/CommandIntentStore.cs`; an integration test that
      seeds a record under the 4.1.0 kind and sees it resumed.
      Done: records are stored under `RecordedIntent`'s type name; `GetDueAsync` reads it and the command envelope's name (claim, renew, keep and completion work by id). `RecordedCommandKindTests.A_command_recorded_under_the_previous_kind_is_still_resumed_and_a_kept_one_is_not_due` passes.
- [x] 1.2 The bus drain does not read a record of the new kind. Verify: an integration test in
      `tests/Stratara.Orleans.IntegrationTests` that records a command through the intent store and runs
      the bus command drain against the same table: nothing is published, the record stays.
      Done: `RecordedCommandKindTests.A_recorded_command_is_invisible_to_the_bus_drain_and_due_for_the_intent_store` reads the table as the bus repository does (by the envelope's kind; the repository itself needs a session context the store fixture has not). Fails against the 4.1.0 store, passes now.

## 2. The drain resumes wherever an intent store is registered (D2)

- [x] 2.1 `OutboxDrainWork` resumes recorded commands when an `ICommandIntentStore` is registered on its
      silo, without requiring `OrleansCommandDispatcher` there, and drains bus kinds as before.
      Verify: `src/Stratara.Orleans/Singleton/OutboxDrainWork.cs`; a test composing a silo with the
      drain and the intent store but no dispatcher, where a due record is resumed and a kept record
      stays kept (scenario *The drain runs on a silo that did not dispatch the commands*).
      Done: the drain resumes when `ICommandIntentStore` resolves — through the dispatcher where one is registered, otherwise through `IntentResumer.Create` from the silo's options and grain factory, skipping while a replay is active. With an intent store the command bus pass is skipped as before and bundles are drained (design D2 said "bus kinds"; a silo with an intent store drains bundles only, as it did with the dispatcher). Proven at unit level with the real drain and a mocked store: `RecordedCommandDrainTests.A_drain_with_an_intent_store_and_no_dispatcher_resumes_the_due_commands` (fails against the 4.1.0 drain). The kept record's exclusion is the store's and is covered by 1.1.
- [x] 2.2 A drain without an intent store that finds records of the execution model's kind logs a
      source-generated warning naming `AddStrataraIntentStore`, with an event id from
      `src/Stratara.Diagnostics/LogEvents.cs`. Verify: a unit test in `tests/Stratara.Orleans.Tests` on
      the logged event.
      Done: `LogEvents.Orleans.RecordedCommandsWithoutIntentStore` = `117_111`, `OrleansLog.LogRecordedCommandsWithoutIntentStore`; `RecordedCommandDrainTests.A_drain_without_an_intent_store_that_finds_recorded_commands_warns`. `SurfaceTrimTests` sets up the new query on its repository mock.
- [x] 2.3 `docs/guides/migrate-to-the-orleans-execution-model.md` registers `AddStrataraIntentStore`
      on the silos that run the drain, and states the upgrade order (stop bus drains first). Verify: the
      per-role table and the upgrade note; documentation tests.
      Done: the per-role row registers the intent store with the drain on the silos, names the warning and the grace; the retirement paragraph states the upgrade order. Documentation tests 699/699.

## 3. Heavy commands leave the aggregate's order (D3)

- [x] 3.1 The dispatcher and the resumer key a heavy hand-over by its intent id. Verify:
      `src/Stratara.Orleans/Aggregates/OrleansCommandDispatcher.cs`; a unit test on the lane keys in
      `tests/Stratara.Orleans.Tests`.
      Done: `AggregateSendLane.KeyOf` used by the dispatcher and the resumer; `RecordedCommandDrainTests.A_heavy_hand_over_is_ordered_under_its_intent_and_any_other_under_its_aggregate`.
- [x] 3.2 Two due heavy intents on one aggregate, followed by a due command on another aggregate, are all
      handed over in one resume pass while the first heavy unit still runs. Verify: an integration test
      (scenario *Two heavy commands to one aggregate are due*).
      Done: `RecordedCommandDrainTests.Two_due_heavy_commands_on_one_aggregate_hold_back_nothing_after_them` — unit level, with heavy units that never finish; against the aggregate key it times out.
- [x] 3.3 A command dispatched while a heavy command on the same aggregate runs does not wait for it.
      Verify: an integration test (scenario *A heavy command and a command name one aggregate*, first half).
      Done: `HeavyOrderTests.A_command_after_a_heavy_command_on_its_aggregate_does_not_wait_for_it` — 6,275 ms
      against the aggregate key, well under 3 s now.
- [x] 3.5 Where a heavy command and a command on the same aggregate both append, one conflict is observed and
      the refused command is resumed. Verify: an integration test with aggregate-appending probes (scenario
      *A heavy command and a command name one aggregate*, second half).
      Done: split from 3.3 during apply. `HeavyConflictTests.A_heavy_append_refused_by_a_concurrent_append_is_resumed_and_succeeds`
      — the heavy handler appends and holds 3 s, a command on the same aggregate appends meanwhile; exactly one
      `ConcurrencyException`, the heavy command resumed by the drain and saved on its second attempt, stream
      version 3. The behaviour is the store's version constraint and the resume, unchanged by this change; the
      test records what the specification now states.

## 4. A timer fires once however long its handler runs (D4)

- [x] 4.1 `TimerOwnerGrain` ignores a tick for a reminder whose handler is running in the activation,
      and confirms the reminder is still registered before calling the handler; the running set is
      cleared in a `finally`. Verify: `src/Stratara.Orleans/Timers/TimerOwnerGrain.cs`.
      Done: `_firing` set, `FireAsync`, the reminder re-read before the handler.
- [x] 4.2 A timer whose handler outlasts the retry period fires once. Verify: an integration test in
      `tests/Stratara.Orleans.IntegrationTests/Timers` with a short retry period and a handler longer than
      two periods (scenario *A timer's handler runs longer than the retry period*); run it against the old
      code first and record that it fails there.
      Done: `LongHandlerTimerTests.A_handler_that_outlasts_the_reminder_call_runs_once`. With a 4 s handler and the default 30 s response timeout the 4.1.0 grain already fired once — the runtime waits for the tick — so the test shortens the response timeout to 2 s and runs a 7 s handler: 3 starts against the 4.1.0 grain, 1 now. The operations guide says the case is a handler that outlasts the response timeout.
- [x] 4.3 The existing timer kill tests stay green. Verify: the `Timers` integration namespace.
      Done: `Timers` 4/4, kill tests included.

## 5. Close

- [x] 5.1 `CHANGELOG.md` `[Unreleased]`: *Fixed* for the drain and the timer, *Changed* for heavy
      commands' order. Verify: the entries.
      Done: *Changed*: heavy commands, recorded-command kind; *Fixed*: drain, timer.
- [x] 5.2 `./scripts/local-gauntlet.sh` green; the Orleans integration namespaces touched (`Aggregates`,
      `HeavyWork`, `Outbox`, `Singleton`, `Timers`) green against PostgreSQL. Verify: the run output,
      recorded here.
      Done 2026-09-16: gauntlet green; `Aggregates` 12/12, `HeavyWork` 3/3 (plus `HeavyOrderTests` and
      `HeavyConflictTests` run on their own), `Outbox` 4/4, `Singleton` 2/2, `Timers` 4/4; Orleans unit tests 86/86;
      documentation tests 699/699.
- [x] 5.3 `openspec validate close-the-orleans-readiness-gaps --strict` passes. Verify: the output.
      Done: "Change 'close-the-orleans-readiness-gaps' is valid".
