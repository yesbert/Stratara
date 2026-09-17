## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. Timer re-registration (D2)

- [x] 1.1 Failing test first: a timer host whose handler re-registers its own owner and purpose with
      `due.DueAt` and returns; assert the timer is listed afterwards and fires again within a retry period.
      Verify: `tests/Stratara.Orleans.IntegrationTests/Timers/ReRegisteredTimerTests.cs`; run against today's
      code and record the empty list.
      *Recorded 2026-09-17.* Against the previous grain the handler fires once and the owner lists no timer.
- [x] 1.2 `_renewed` in `TimerOwnerGrain`; the unregister after the handler skips a renewed name; both sets
      cleared together. Verify: `src/Stratara.Orleans/Timers/TimerOwnerGrain.cs`; 1.1 green; the second test
      (`due.DueAt + 2 s`, exactly one timer, the later one) green.

## 2. Owner-id validation (D3)

- [x] 2.1 Failing test first: `IDurableTimers.RegisterAsync` with an owner id of 200 characters throws
      `ArgumentException` naming the limit; `CancelAsync`, `CancelAllAsync` and `ListAsync` likewise; a unit
      test renders a `GrainId` for the timer grain with a key at the limit and one over, against 150.
      Verify: `tests/Stratara.Orleans.Tests/SurfaceTrimTests.cs` (beside the purpose tests).
      *Recorded 2026-09-17.* The limit is 139: the runtime renders the timer grain's id as `timerowner/<key>`. The unit
      test renders a `GrainId` of that type; `ReRegisteredTimerTests` also registers and lists a timer for an owner id
      of exactly 139 characters against the PostgreSQL reminder table, so the constant is checked against the runtime.
- [x] 2.2 `ReminderName.MaxOwnerIdLength` and `EnsureValidOwnerId`; `DurableTimers` validates on every member;
      `IDurableTimers` XML documents the exception. Verify: `TimerOwnerGrain.cs`, `DurableTimers.cs`,
      `src/Stratara.Abstractions/Abstractions/Timers/IDurableTimers.cs`; 2.1 green.

## 3. The stop token (D1)

- [x] 3.1 `LogEvents.Orleans.HandlerStoppedWithSilo = 117_114` and its `[LoggerMessage]` in `OrleansLog`.
      Verify: `src/Stratara.Diagnostics/LogEvents.cs`, `src/Stratara.Orleans/Diagnostics/OrleansLog.cs`;
      `DiagnosticsTests` or the log-events reference test.
      *Recorded 2026-09-17.* The event is Information, so it took the band's next Information id, `117_008`, not
      `117_114`.
- [x] 3.2 Failing tests first, one class: a two-silo cluster on the PostgreSQL store and reminder table with
      `DeactivationTimeout = 2 s`; (a) a timer handler awaiting its token — stop silo one, assert the handler
      observed cancellation, the timer is still listed, and fires on silo two; (b) a recorded command's handler
      awaiting its token — assert it observed cancellation and the command completed on silo two with one
      attempt recorded; (c) a forwarded command's handler awaiting its token — assert the caller's failure
      message names the stop; (d) a handler ignoring the token that takes less than a long budget — assert it
      completed before `StopAsync` returned. Verify: `tests/Stratara.Orleans.IntegrationTests/Hosting/StoppingSiloTests.cs`;
      (a)–(c) recorded as failing against today's code (the handlers never observe a token).
      *Recorded 2026-09-17.* (a)–(c) fail against the previous code, each handler observing no cancellation; (d) passes
      there too. The tests also assert the `117_008` entry with the owner and purpose, the command type, or the
      aggregate.
- [x] 3.3 `CommandExecution.RunAsync` / `RunIntentAsync` / `InvokeAsync` take a `CancellationToken` to the
      invoker; `AggregateGrain`, `CommandRunnerGrain` and `HeavyWorkGrain` hold `_stopping`, pass its token,
      track the running handlers, and wait-then-cancel-then-wait on deactivation; the forwarded command's
      failure message; the heavy slot and permit waits take the token; the log event on every path. Verify:
      the four files; 3.2 (b)–(d) green; `LongOrderTests`, `HeavyNonYieldingTests`, `DurableIntentTests` still
      green.
      *Recorded 2026-09-17.* **Deviation from D1's mechanism, not its behaviour.** Cancelling from `OnDeactivateAsync`
      never reached a handler: on a graceful stop the runtime waits in its grain-deactivation stage for the requests
      an activation is running — up to thirty seconds, then "some grains failed to deactivate promptly" — and calls
      `OnDeactivateAsync` only after them. The token is therefore a `SiloStopSignal` in the silo lifecycle's `Active`
      stage: when the silo begins to stop it requests cancellation after `GrainCollectionOptions.DeactivationTimeout`,
      and every grain's `_stopping` is linked to it. `OnDeactivateAsync` keeps the wait-then-cancel for an activation
      deactivated outside a silo stop. The polite-then-firm order and the budget are the design's; D1's rejection of
      `ApplicationStopping` is about the host-wide event before the silo moves activations, which this is not.
- [x] 3.4 `TimerOwnerGrain.FireAsync` passes the token to `ExistsAsync` and `OnDueAsync`; the grain tracks
      in-flight ticks and overrides `OnDeactivateAsync` the same way; the log event. Verify: the file; 3.2 (a)
      green; `LongHandlerTimerTests`, `HardKillTimerTests`, `OwnerCheckedTimerTests` still green.

## 4. Documentation (D4)

- [x] 4.1 `docs/guides/operate-the-orleans-execution-model.md`: *Stopping a silo* and the stop case in the
      long-handler paragraph; the re-registration sentence where process timeouts are written;
      `docs/reference/log-events-schema.md` row for `117_114`; `ITimerHandler.OnDueAsync` and `IDurableTimers`
      remarks. Verify: the sections; documentation tests; doc-symbol check.
- [x] 4.2 `CHANGELOG.md` `[Unreleased]` *Changed* (the stop token on every grain path, the forwarded command's
      message, the re-registered timer kept, the owner-id refusal) and *Added* (the log event);
      `src/Stratara.Orleans/README.md`; `llms.txt`. Verify: the entries.

## 5. Close

- [x] 5.1 `./scripts/local-gauntlet.sh` green; the `Timers`, `Aggregates`, `HeavyWork` and `Hosting`
      integration namespaces green against PostgreSQL, Redis and RabbitMQ. Verify: the run output, recorded here.
      *Recorded 2026-09-17.* Local gauntlet green; `Timers`, `Aggregates`, `HeavyWork` and `Hosting` 43 of 43.
- [x] 5.2 `openspec validate carry-cancellation-to-grain-handlers-and-harden-timer-registration --strict` passes.
      Verify: the output.
      *Recorded 2026-09-17.* The first validation refused the delta: it had been written before
      `keep-a-store-reader-batch-tenant-correct-and-its-checkpoint-guarded` was archived, and dropped that change's
      sentence and scenario on the held-back resumption. Both were copied into the MODIFIED block; the change is valid.
