## 0. Gate

- [ ] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. Timer re-registration (D2)

- [ ] 1.1 Failing test first: a timer host whose handler re-registers its own owner and purpose with
      `due.DueAt` and returns; assert the timer is listed afterwards and fires again within a retry period.
      Verify: `tests/Stratara.Orleans.IntegrationTests/Timers/ReRegisteredTimerTests.cs`; run against today's
      code and record the empty list.
- [ ] 1.2 `_renewed` in `TimerOwnerGrain`; the unregister after the handler skips a renewed name; both sets
      cleared together. Verify: `src/Stratara.Orleans/Timers/TimerOwnerGrain.cs`; 1.1 green; the second test
      (`due.DueAt + 2 s`, exactly one timer, the later one) green.

## 2. Owner-id validation (D3)

- [ ] 2.1 Failing test first: `IDurableTimers.RegisterAsync` with an owner id of 200 characters throws
      `ArgumentException` naming the limit; `CancelAsync`, `CancelAllAsync` and `ListAsync` likewise; a unit
      test renders a `GrainId` for the timer grain with a key at the limit and one over, against 150.
      Verify: `tests/Stratara.Orleans.Tests/SurfaceTrimTests.cs` (beside the purpose tests).
- [ ] 2.2 `ReminderName.MaxOwnerIdLength` and `EnsureValidOwnerId`; `DurableTimers` validates on every member;
      `IDurableTimers` XML documents the exception. Verify: `TimerOwnerGrain.cs`, `DurableTimers.cs`,
      `src/Stratara.Abstractions/Abstractions/Timers/IDurableTimers.cs`; 2.1 green.

## 3. The stop token (D1)

- [ ] 3.1 `LogEvents.Orleans.HandlerStoppedWithSilo = 117_114` and its `[LoggerMessage]` in `OrleansLog`.
      Verify: `src/Stratara.Diagnostics/LogEvents.cs`, `src/Stratara.Orleans/Diagnostics/OrleansLog.cs`;
      `DiagnosticsTests` or the log-events reference test.
- [ ] 3.2 Failing tests first, one class: a two-silo cluster on the PostgreSQL store and reminder table with
      `DeactivationTimeout = 2 s`; (a) a timer handler awaiting its token — stop silo one, assert the handler
      observed cancellation, the timer is still listed, and fires on silo two; (b) a recorded command's handler
      awaiting its token — assert it observed cancellation and the command completed on silo two with one
      attempt recorded; (c) a forwarded command's handler awaiting its token — assert the caller's failure
      message names the stop; (d) a handler ignoring the token that takes less than a long budget — assert it
      completed before `StopAsync` returned. Verify: `tests/Stratara.Orleans.IntegrationTests/Hosting/StoppingSiloTests.cs`;
      (a)–(c) recorded as failing against today's code (the handlers never observe a token).
- [ ] 3.3 `CommandExecution.RunAsync` / `RunIntentAsync` / `InvokeAsync` take a `CancellationToken` to the
      invoker; `AggregateGrain`, `CommandRunnerGrain` and `HeavyWorkGrain` hold `_stopping`, pass its token,
      track the running handlers, and wait-then-cancel-then-wait on deactivation; the forwarded command's
      failure message; the heavy slot and permit waits take the token; the log event on every path. Verify:
      the four files; 3.2 (b)–(d) green; `LongOrderTests`, `HeavyNonYieldingTests`, `DurableIntentTests` still
      green.
- [ ] 3.4 `TimerOwnerGrain.FireAsync` passes the token to `ExistsAsync` and `OnDueAsync`; the grain tracks
      in-flight ticks and overrides `OnDeactivateAsync` the same way; the log event. Verify: the file; 3.2 (a)
      green; `LongHandlerTimerTests`, `HardKillTimerTests`, `OwnerCheckedTimerTests` still green.

## 4. Documentation (D4)

- [ ] 4.1 `docs/guides/operate-the-orleans-execution-model.md`: *Stopping a silo* and the stop case in the
      long-handler paragraph; the re-registration sentence where process timeouts are written;
      `docs/reference/log-events-schema.md` row for `117_114`; `ITimerHandler.OnDueAsync` and `IDurableTimers`
      remarks. Verify: the sections; documentation tests; doc-symbol check.
- [ ] 4.2 `CHANGELOG.md` `[Unreleased]` *Changed* (the stop token on every grain path, the forwarded command's
      message, the re-registered timer kept, the owner-id refusal) and *Added* (the log event);
      `src/Stratara.Orleans/README.md`; `llms.txt`. Verify: the entries.

## 5. Close

- [ ] 5.1 `./scripts/local-gauntlet.sh` green; the `Timers`, `Aggregates`, `HeavyWork` and `Hosting`
      integration namespaces green against PostgreSQL, Redis and RabbitMQ. Verify: the run output, recorded here.
- [ ] 5.2 `openspec validate carry-cancellation-to-grain-handlers-and-harden-timer-registration --strict` passes.
      Verify: the output.
