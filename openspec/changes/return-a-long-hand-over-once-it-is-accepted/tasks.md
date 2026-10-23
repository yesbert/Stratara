## 1. The command runner answers on acceptance

- [x] 1.1 `CommandRunnerGrain.ExecuteIntentAsync` (now `[AlwaysInterleave]`) starts the lease, keeps it, and issues a new `[OneWay]` `RunAcceptedAsync` to itself, which runs `CommandExecution.RunIntentAsync` with that lease. A lease that does not start, or a hand-over the activation already holds, is answered as accepted without running (`src/Stratara.Orleans/Aggregates/AggregateGrain.cs`, `ICommandRunnerGrain` and `CommandRunnerGrain`)
- [x] 1.2 Deactivation still cancels and waits for the one-way run, and gives back the attempt of an accepted run that never started (`CommandRunnerGrain.OnDeactivateAsync`, `AbandonAsync`)

## 2. The heavy pool answers on acceptance

- [x] 2.1 `HeavyWorkGrain.ExecuteIntentAsync` returns after the unit is queued. Removing it from `_held`/`_inFlight` and disposing its scope move to `SettleAsync`, awaited on the activation (`src/Stratara.Orleans/Aggregates/HeavyWorkGrain.cs`)
- [x] 2.2 `[OneWay, AlwaysInterleave]` `HoldAsync` on `IHeavyWorkGrain`: at most one outstanding, and it waits until no unit in flight is still running (design D2)
- [x] 2.3 Found while proving 4.3, and present before this change: `HeavyWorkRunner.StopAsync` waited for running units, but the host stops the runner before the silo whose stop cancels them, so a silo stop hung until the host's shutdown timeout. The runner now only stops taking units (design D5) (`src/Stratara.Orleans/Aggregates/HeavyWorkRunner.cs`)

## 2a. Review findings (`/code-review high`; Copilot's quota was exhausted)

- [x] 2a.1 An `OperationCanceledException` the silo's stop did not cause is recorded and logged as a failed attempt. Before this, it went nowhere once the hand-over no longer carried it back (`CommandExecution.RunIntentAsync`; `IntentFailureLoggingTests.A_cancellation_the_silo_did_not_cause_is_a_failed_attempt_logged_and_recorded`)
- [x] 2a.2 The runner logs a duplicate hand-over it drops while it holds the intent (`IntentHandOverDropped`, Debug), and gives up a hand-over accepted while its silo stops, as the aggregate grain does. The assertion in `ResumedOnceInOrderTests.Two_hand_overs_of_one_claim_run_the_handler_once` is restored
- [x] 2a.3 The heavy pool's hold heals itself: the guard is "a hold is running" rather than "a hold was sent", so a hold message that was lost is sent again with the next hand-over, and one that cannot be sent does not fail the hand-over
- [x] 2a.4 `HeavyWorkRunner` keeps a start flag instead of worker tasks nobody awaits, `HoldAsync` scans its units once per round, and the upgrade note says to upgrade silos first, naming the `HandOverFailed` warning a new dispatcher logs against an old silo

## 3. The dispatcher logs every failed hand-over

- [x] 3.1 Remove the `spansTheUnit` exception from `IntentHandOver.Observe` and rewrite its XML summary (`src/Stratara.Orleans/Aggregates/OrleansCommandDispatcher.cs`)
- [x] 3.2 Unit test: a heavy or aggregate-less hand-over that faults with `TimeoutException` is logged with `LogHandOverFailed`. The test that pinned the old suppression is turned around (`tests/Stratara.Orleans.Tests/IntentFailureLoggingTests.cs`, `A_timeout_on_any_hand_over_is_logged_because_a_hand_over_answers_once_the_command_is_accepted`)

## 4. Tests

- [x] 4.1 *A heavy command runs longer than the response timeout* and *A command that names no aggregate runs longer than the response timeout*: silo plus client host, two-second `ResponseTimeout` on both, five-second handlers, bus replaced by one that accepts, logs captured on both. The test asserts one run, a committed append, completed records, and no `Response did not arrive on time` / `Dropping expired message`. It fails on the previous code on the log assertion only (`tests/Stratara.Orleans.IntegrationTests/Aggregates/LongHandOverTests.cs`)
- [x] 4.2 *A resumed long command*: the same, recorded without a hand-over and resumed by the drain (`LongHandOverTests.cs`, `Resumed_long_commands_run_once_and_leave_no_timeout_in_the_log`)
- [x] 4.3 *The silo stops while an accepted long command runs*: heavy and aggregate-less variants. The handler is cancelled by the stop, the record is kept, and the command is resumed on the remaining silo and completed (`tests/Stratara.Orleans.IntegrationTests/Hosting/StoppingSiloTests.cs`)
- [x] 4.4 *The hand-over itself fails*: covered by 3.2 and `A_hand_over_that_faults_is_logged_with_the_intent_and_the_aggregate` (`IntentFailureLoggingTests.cs`)
- [x] 4.5 The full `Stratara.Orleans.IntegrationTests` suite stays green, including `HeavyWork/*`, `IntentClaimTests`, `ResumedOnceInOrderTests`, `ForwardedTimeoutTests` and `LongOrderTests`

## 5. Documentation and release notes

- [x] 5.1 `docs/guides/operate-the-orleans-execution-model.md`, *The response timeout*: a recorded command's hand-over returns on acceptance and is not bounded by the timeout. A hand-over that times out is logged. "It is not logged" is removed
- [x] 5.2 `docs/guides/migrate-to-the-orleans-execution-model.md`, *Register the silo*: the ADO.NET provider packages ship no scripts. The three PostgreSQL scripts are listed with their paths in `dotnet/orleans` at the release tag
- [x] 5.3 Checked `docs/concepts/orleans-execution-model.md`, the guides and `llms.txt` for either claim. There are no further hits
- [x] 5.4 `CHANGELOG.md`: entry under `[Unreleased]`

## 6. Verification

- [x] 6.1 `openspec validate return-a-long-hand-over-once-it-is-accepted --strict`
- [x] 6.2 `./scripts/local-gauntlet.sh` green
- [x] 6.3 `dotnet test tests/Stratara.Orleans.IntegrationTests` green (Docker)
