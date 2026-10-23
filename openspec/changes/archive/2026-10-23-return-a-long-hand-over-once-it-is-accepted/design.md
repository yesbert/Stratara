## Context

See `proposal.md`, *Why*. Read on `main` at `065d14b` (`v4.4.6`):

- **The dispatcher hands over three ways** (`IntentHandOver.HandOverAsync`, `OrleansCommandDispatcher.cs:140-149`):
  - an aggregate command goes to `IAggregateGrain.AcceptIntentAsync`;
  - a heavy command goes to `IHeavyWorkGrain.ExecuteIntentAsync` on a pool spread by intent id;
  - a command that names no aggregate goes to `ICommandRunnerGrain.ExecuteIntentAsync`, keyed by the intent.
- **Only the aggregate path answers on acceptance.** `AcceptIntentAsync` returns once the hand-over is
  renewed. The order runs through `RunAcceptedAsync`, a `[OneWay]` call the grain makes to itself
  (`AggregateGrain.cs:44-46`, `:246`). A one-way call carries no response and so no response timeout, and
  while it runs the activation has a request in flight and is not collected as idle.
- **The heavy pool accepts early but answers late.** `HeavyWorkGrain.ExecuteIntentAsync` starts the
  intent's lease at once (`IntentLease.StartAsync`) and queues the unit on the silo's `HeavyWorkRunner`,
  off the activation's scheduler. It then `await`s the run (`HeavyWorkGrain.cs:111-153`). Everything that
  must happen after the handler already happens on the silo, inside `CommandExecution.RunIntentAsync`:
  ending the lease, recording a failure, queueing the completion. The caller learns nothing from the
  answer except that the unit ended.
- **The runner neither accepts early nor answers early.** `CommandRunnerGrain.ExecuteIntentAsync` returns
  `CommandExecution.RunAsync(...)`, which starts the lease and runs the handler in one awaited call
  (`AggregateGrain.cs:340-349`, `:376-391`).
- **The dispatcher hides the consequence from its own log only.** `IntentHandOver.Observe`
  (`OrleansCommandDispatcher.cs:154-178`) skips logging a `TimeoutException` when the hand-over is heavy or
  names no aggregate. The Orleans runtime logs the same timeout on the caller (`Response did not arrive on
  time`) and drops the late status and response messages on the silo with warnings. A consumer reported
  both on Orleans 10.3.1.
- **Deactivation cancels what runs.** Both grains cancel `_stopping` and wait within the budget in
  `OnDeactivateAsync` (`AggregateGrain.StopRunningAsync`), so a unit stopped by a silo stop is resumed from
  its record.
- **Tests that already shorten the timeout:** `ForwardedTimeoutTests`, `LongOrderTests`, `SendCycleTests`
  (`tests/Stratara.Orleans.IntegrationTests/Aggregates/`).

## Goals / Non-Goals

**Goals:**

- No caller-facing grain call stays open for the length of a heavy unit or a command that names no aggregate.
- Lease, permit, completion, failure, resumption and stop behave exactly as they do today.
- A hand-over timeout, now rare, is logged rather than suppressed.

**Non-Goals:**

- The forwarded command. It is a call whose caller wants the handler's outcome, and the spec keeps it
  bounded by the response timeout on purpose (*An accepted command is recorded before the call returns and
  resumed after a crash*).
- Changing the Orleans runtime's own log levels or filters. Hiding the runtime's warnings would also hide
  real ones.
- Moving the runner onto the heavy pool's workers. A command that names no aggregate is not heavy and keeps
  its own activation per intent.

## Decisions

### D1: Answer on acceptance, run through a one-way self-call, as the aggregate grain does

Both grains answer their hand-over once they hold the unit and the lease has started. The run continues
through a `[OneWay]` call the grain makes to itself:

- **Runner:** `ExecuteIntentAsync` starts the lease (`IntentLease.StartAsync`), remembers it, and issues
  `RunAsync()` one-way to itself. That call runs `CommandExecution.RunIntentAsync` with the lease. A lease
  that does not start (taken or completed) is answered as accepted and nothing runs, as today.
- **Heavy pool:** `ExecuteIntentAsync` keeps its acceptance and queueing and stops awaiting `run`. The
  `_inFlight` bookkeeping stays. The `finally` that removes the unit and disposes its scope moves to a
  continuation of `run` that is posted back to the activation's scheduler, so `_held` and `_inFlight` are
  still only touched on the activation.

*Why not answer earlier, before the lease?* The lease is what keeps the drain from handing the command over
again. Answering before it starts would let a fast resumption overlap with an acceptance in progress, and the
spec forbids a second hand-over while a command is running. The lease start is a single write, well inside
any response timeout.

*Alternative rejected: keep the call open and raise the timeout.* Moving `ResponseTimeout` up for every
host to cover the longest heavy unit also lengthens the wait for a forwarded command that is genuinely lost.
The guide already tells hosts to size it for forwarded commands only.

*Alternative rejected: a one-way hand-over from the dispatcher.* Making `ExecuteIntentAsync` itself
`[OneWay]` removes the timeout too, but the dispatcher would no longer see a hand-over that failed, and the
spec requires that failure to be logged. Answering on acceptance keeps it.

### D2: The heavy pool's activation is held by a call to itself while units are in flight

Once `ExecuteIntentAsync` returns, the heavy pool's activation has no request in flight while its units run
on the runner's workers. Idle collection could then deactivate it after the collection age, and
`OnDeactivateAsync` would cancel units that are still healthy. While `_inFlight` is non-empty, the grain
therefore keeps exactly one `[OneWay, AlwaysInterleave]` call to itself outstanding (`HoldAsync`). That call
awaits `Task.WhenAll(_inFlight)` and re-issues itself while units remain. It interleaves, so it never stops
a further hand-over from being accepted, which the spec requires (*Heavy work is bounded across the cluster
…*: no further heavy command kept from being accepted).

The runner needs no such hold: its one-way `RunAsync` is itself the request in flight for the length of the
unit, exactly like `RunAcceptedAsync`.

*Alternative rejected: `DelayDeactivation`.* It takes a fixed span, and a unit has no known length. A
renewing timer would work but is a second mechanism beside the one the aggregate grain already uses.

### D3: `Observe` logs every failed hand-over alike

The `spansTheUnit` exception in `IntentHandOver.Observe` is removed, with its remark. After D1 a
`TimeoutException` on any hand-over means the silo did not accept in time, which is the case the spec wants
seen (*every attempt that fails … and so SHALL a hand-over that fails*). The existing log event
(`LogHandOverFailed`) is reused and no event id changes.

### D4: Evidence for "no timeout logged" is read from the Orleans log categories

The new integration test runs a silo and a client with a shortened `ResponseTimeout`, as
`ForwardedTimeoutTests` does. It captures the providers' logs on both hosts and asserts that no entry
contains `Response did not arrive on time` or `Dropping expired message`. Asserting on message text is
brittle across Orleans versions, so the test also asserts on the observable outcome (the intent completed,
the append committed, one handler run). A wording change in Orleans can make the log assertion pass
vacuously but cannot make a regression in behaviour pass. That limit is accepted and named in the test.

### D5: The heavy-work runner does not wait for running units when the host stops it

Found while proving *The silo stops while an accepted long command runs*. The defect is present on `v4.4.6`, and
the new test fails there the same way. `HeavyWorkRunner` is a hosted service registered after `UseOrleans`, so the
host stops it **before** the silo. Its `StopAsync` waited for the units its workers held. A unit is cancelled only
through the silo's stop signal, which fires once the silo itself stops, so the host's stop hung until
`HostOptions.ShutdownTimeout` and the handler was not cancelled within the deactivation budget. `StopAsync` now only
completes the channel. The pool grain's `OnDeactivateAsync` already waits for `_inFlight` within the budget and then
cancels, which is the behaviour the spec states for every recorded command (*An accepted command is recorded …*).
The proof is `StoppingSiloTests.A_recorded_heavy_command_whose_handler_waits_on_its_token_observes_the_stop_and_completes_on_the_remaining_silo`.

*Alternative rejected: register the runner before the silo so it stops after.* Hosted-service order depends on the
host's own call order, which the framework does not control.

## Risks / Trade-offs

- **[A unit's failure is no longer visible on the dispatching host as a faulted call]** → It never was in a
  useful way. The dispatcher already logged nothing for the long-call case, and the record, the silo's
  failure log and the resumption are where a failure is seen (spec: every failed attempt is logged with
  the command's identity). No behaviour a caller relied on is removed.
- **[The hold call re-issues itself in a loop]** → It re-issues only while `_inFlight` is non-empty and at
  most one is outstanding (guarded by a flag on the activation). A unit that never ends holds the activation,
  which is what the current awaiting call also does.
- **[A continuation touches grain state off the scheduler]** → It is posted back to the activation's task
  scheduler (D1), and covered by the existing heavy-burst tests (*A heavy burst queues commands longer than
  the grace*), which exercise `_held` and `_inFlight` under load.

- **[A hand-over that arrives while an earlier one of the same intent is still starting its lease is answered as
  accepted; if the earlier lease is then refused, nothing runs until the next grace]** → This is the aggregate grain's
  behaviour too (`_heldIntents`), and a lease starts with a single write, far inside the grace. The drain hands the
  command over again after the grace, and the attempt bound still applies. Accepted for consistency, and found in
  review rather than observed.

## Migration Plan

No data, schema or configuration change. Method names and aliases stay, and the runner gains one new
one-way method.

- **Dispatcher on this release, silo on the previous one:** the old grain awaits the unit as before. Nothing
  breaks, but the caller sees the old runtime warnings, and also its own `HandOverFailed` warning, because D3
  removed the suppression. Upgrade the silos first, as the changelog says.
- **Dispatcher on the previous release, silo on this one:** the hand-over is answered sooner. The old
  `Observe` suppression has nothing left to suppress.
- **A runner hand-over across mixed silos:** the runner's self-call goes to its own activation, so both
  methods are always on one silo of one release.

Rollback is redeploying the previous release. No state outlives a unit.
