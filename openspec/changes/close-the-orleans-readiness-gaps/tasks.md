## 0. Gate

- [ ] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. Recorded commands have a kind of their own (D1)

- [ ] 1.1 The intent store records commands under the execution model's own kind, and its reads, claim,
      keep and completion accept the new kind and the 4.1.0 kind. Verify:
      `src/Stratara.Orleans.EntityFrameworkCore/Intents/CommandIntentStore.cs`; an integration test that
      seeds a record under the 4.1.0 kind and sees it resumed.
- [ ] 1.2 The bus drain does not read a record of the new kind. Verify: an integration test in
      `tests/Stratara.Orleans.IntegrationTests` that records a command through the intent store and runs
      the bus command drain against the same table: nothing is published, the record stays.

## 2. The drain resumes wherever an intent store is registered (D2)

- [ ] 2.1 `OutboxDrainWork` resumes recorded commands when an `ICommandIntentStore` is registered on its
      silo, without requiring `OrleansCommandDispatcher` there, and drains bus kinds as before.
      Verify: `src/Stratara.Orleans/Singleton/OutboxDrainWork.cs`; a test composing a silo with the
      drain and the intent store but no dispatcher, where a due record is resumed and a kept record
      stays kept (scenario *The drain runs on a silo that did not dispatch the commands*).
- [ ] 2.2 A drain without an intent store that finds records of the execution model's kind logs a
      source-generated warning naming `AddStrataraIntentStore`, with an event id from
      `src/Stratara.Diagnostics/LogEvents.cs`. Verify: a unit test in `tests/Stratara.Orleans.Tests` on
      the logged event.
- [ ] 2.3 `docs/guides/migrate-to-the-orleans-execution-model.md` registers `AddStrataraIntentStore`
      on the silos that run the drain, and states the upgrade order (stop bus drains first). Verify: the
      per-role table and the upgrade note; documentation tests.

## 3. Heavy commands leave the aggregate's order (D3)

- [ ] 3.1 The dispatcher and the resumer key a heavy hand-over by its intent id. Verify:
      `src/Stratara.Orleans/Aggregates/OrleansCommandDispatcher.cs`; a unit test on the lane keys in
      `tests/Stratara.Orleans.Tests`.
- [ ] 3.2 Two due heavy intents on one aggregate, followed by a due command on another aggregate, are all
      handed over in one resume pass while the first heavy unit still runs. Verify: an integration test
      (scenario *Two heavy commands to one aggregate are due*).
- [ ] 3.3 A command dispatched while a heavy command on the same aggregate runs does not wait for it;
      where both append, one conflict is observed and the refused command is resumed. Verify: an
      integration test (scenario *A heavy command and a command name one aggregate*).
- [ ] 3.4 The XML documentation of `HeavyWorkGrain`, `OrleansCommandDispatcher` and the heavy-work
      options, `docs/concepts/orleans-execution-model.md` (*one writer per aggregate*) and
      `docs/guides/operate-the-orleans-execution-model.md` say heavy commands run outside the
      aggregate's turn and order, and advise against heavy commands on aggregates with a steady stream of
      interactive commands. Verify: a read of each; documentation tests.

## 4. A timer fires once however long its handler runs (D4)

- [ ] 4.1 `TimerOwnerGrain` ignores a tick for a reminder whose handler is running in the activation,
      and confirms the reminder is still registered before calling the handler; the running set is
      cleared in a `finally`. Verify: `src/Stratara.Orleans/Timers/TimerOwnerGrain.cs`.
- [ ] 4.2 A timer whose handler outlasts the retry period fires once. Verify: an integration test in
      `tests/Stratara.Orleans.IntegrationTests/Timers` with a short retry period and a handler longer than
      two periods (scenario *A timer's handler runs longer than the retry period*); run it against the old
      code first and record that it fails there.
- [ ] 4.3 The existing timer kill tests stay green. Verify: the `Timers` integration namespace.

## 5. Close

- [ ] 5.1 `CHANGELOG.md` `[Unreleased]`: *Fixed* for the drain and the timer, *Changed* for heavy
      commands' order. Verify: the entries.
- [ ] 5.2 `./scripts/local-gauntlet.sh` green; the Orleans integration namespaces touched (`Aggregates`,
      `HeavyWork`, `Outbox`, `Singleton`, `Timers`) green against PostgreSQL. Verify: the run output,
      recorded here.
- [ ] 5.3 `openspec validate close-the-orleans-readiness-gaps --strict` passes. Verify: the output.
