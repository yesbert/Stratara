## Context

See `proposal.md` — Why. The current code:

- **Command paths.** `CommandExecution.InvokeAsync` (`src/Stratara.Orleans/Aggregates/AggregateGrain.cs:293-311`)
  dispatches with `CancellationToken.None` on both branches (`:307-309`); nothing above it takes a
  token — `RunAsync` (`:243-256`), `RunIntentAsync` (`:263-282`), `AggregateGrain.RunAcceptedAsync`
  (`:101-129`), `CommandRunnerGrain.ExecuteIntentAsync` (`:220-223`), `HeavyWorkGrain.ExecuteIntentAsync`
  (`HeavyWorkGrain.cs:109-121`). `AggregateGrain.OnDeactivateAsync` (`:131-135`) abandons the
  *queued* commands (`AbandonAsync`, `:182-205`) and does not wait for the one running.
  `RunIntentAsync` records a failure for every exception except `OperationCanceledException` (`:274-278`);
  the lease is disposed either way, so the drain resumes the intent after the grace.
- **Timer path.** `TimerOwnerGrain.FireAsync` (`src/Stratara.Orleans/Timers/TimerOwnerGrain.cs:128-150`)
  calls `owners.ExistsAsync(ownerId, CancellationToken.None)` (`:135`) and
  `handler.OnDueAsync(new TimerDue(...), CancellationToken.None)` (`:147`), then
  `UnregisterByNameAsync(reminderName)` (`:149`, `:152-159`), which fetches the reminder by name and
  unregisters whatever carries it. `RegisterAsync` (`:41-61`) takes the change gate, cancels every
  reminder of the purpose (`CancelPurposeAsync`, `:93-103`) and registers `ReminderName.Encode(purpose, dueAt)`
  — the same name when the due time is the same. `_firing` (`:39`, `:111-126`) drops a tick for a name
  whose handler still runs. The grain is `[Reentrant]` (`:23`) and overrides no `OnDeactivateAsync`.
- **Validation.** `ReminderName.EnsureValidPurpose` (`:173-187`) refuses an empty purpose, one with
  `@`, or one longer than `MaxPurposeLength = 130` (`:169`), naming the limit and the length;
  `DurableTimers.RegisterAsync` (`DurableTimers.cs:8-13`) calls it and nothing checks the owner id on
  any member; the owner id is the grain key (`:27`). The reminder table's grain-id column is 150
  characters (the ADO.NET scripts); the stored value is the runtime's string form of the grain id,
  which prefixes the key with the grain type's name. `IDurableTimers.RegisterAsync` documents the
  purpose limit (`IDurableTimers.cs:22`).
- **The pattern to mirror.** `StoreReaderGrain` holds a `CancellationTokenSource _stopping` (`StoreReaderGrain.cs:34`),
  runs every loop under its token (`:122`), and on deactivation waits for the running loop with the
  deactivation token, then cancels and waits once more with none (`:96-116`). `SingletonWorkGrain.RunOnceAsync`
  receives the grain timer's token (`SingletonWorkGrain.cs:80-85`).
- **The runtime.** `OnDeactivateAsync(DeactivationReason, CancellationToken)` — the token "signals when
  deactivation should complete promptly"; `GrainCollectionOptions.DeactivationTimeout` (30 s) bounds
  the deactivation; `OnDeactivateAsync` is not called on a hard death. Orleans cancellation is
  cooperative. `SiloMessagingOptions.ResponseTimeout` bounds a forwarded call; with
  `CancelRequestOnTimeout` the runtime cancels a timed-out request's token.
- **Tests.** `LongHandlerTimerTests.cs:55-72` is an in-process silo with PostgreSQL reminders whose
  handler waits on `Task.Delay(duration, cancellationToken)` — it would see a token today if one were
  passed; `RecordingTimerHost.cs`; `HardKillTimerTests.cs`; `DurableIntentTests.cs`;
  `SurfaceTrimTests.cs:30` asserts the purpose limit; `TwoSiloKillTests.cs` (two silos, one lost).

## Goals / Non-Goals

**Goals:**
- A stopping silo is the one thing a handler on a grain path can be told about, on every path, with
  the same meaning: stop; what you did not finish will run again elsewhere.
- Every reminder the timer grain unregisters is the one it fired.
- An owner id is refused where a purpose is, before the store sees it.

**Non-Goals:**
- Carrying the *caller's* token across a forwarded call. Orleans cancels a call's token when the call
  times out (`CancelRequestOnTimeout`), and the specification promises that a command accepted into an
  aggregate's order runs to the end however long the order takes — a caller's timeout must not
  withdraw it. Until the runtime distinguishes a caller's cancel from its own timeout, the caller's
  token stops at the hand-over, as it does today, and the response timeout stays the caller's bound.
- Cancelling a heavy command's wait for a slot or a permit on a caller's behalf; only the stop token
  reaches those waits, so a stopping silo releases them.
- Making `OnDeactivateAsync` reliable on a hard death. It is not, and the crash paths already cover
  that case (lease expiry, reminder retry).
- A timer re-registered from *another* owner's handler or from outside a tick: already correct.

## Decisions

### D1 — One stop token per activation, requested after the deactivation budget, threaded to the handler

Each of the four grains holds a `CancellationTokenSource _stopping` and passes `_stopping.Token` down
to the handler: `CommandExecution.RunAsync` / `RunIntentAsync` / `InvokeAsync` gain a
`CancellationToken` parameter that replaces `CancellationToken.None` in the invoker
(`DispatchAsync` / `InvokeHandlerAsync` already take one); `TimerOwnerGrain.FireAsync` passes it to
`ExistsAsync` and `OnDueAsync`. `OnDeactivateAsync` follows `StoreReaderGrain.cs:96-116`: wait for the
running handlers with the deactivation token; in `finally`, if any still runs, cancel `_stopping` and
wait once more with no token; then the existing abandon of the queued commands. "Running handlers"
is the runner task in `AggregateGrain` and `CommandRunnerGrain`, the set of in-flight units in
`HeavyWorkGrain`, and the set of in-flight ticks in `TimerOwnerGrain` (reentrant: several at once).
The wait is therefore first polite — a handler that finishes within the budget is never cancelled —
and then firm.

What follows the cancel, per path, is what the code already does for the exception:
- **Recorded intent:** `OperationCanceledException` is excluded from `RecordFailureAsync`
  (`:274`), so no attempt is counted; the lease is disposed by `await using`, so renewal stops; the
  completion queue is not reached; the drain resumes after the grace on a silo that still runs. A
  new log event `LogEvents.Orleans.HandlerStoppedWithSilo = 117_114` (information: intent id or
  none, aggregate id, command type or timer owner and purpose) is written where the cancellation is
  caught, so that a second run on the next silo is attributable to a stop.
- **Forwarded command:** the runner catches the `OperationCanceledException` whose token is
  `_stopping.Token` and sets the completion to an `InvalidOperationException` with the message *the
  silo running the aggregate's activation stopped before the handler completed; dispatch the command
  again* — the same shape as the abandon message (`:186`) so a caller handles both alike. A handler
  that had already committed before observing the token runs a second time on re-dispatch, which is
  the at-least-once the handler tolerates on every path.
- **Timer:** the exception propagates out of `FireAsync` before `UnregisterByNameAsync`, so the
  reminder stays and the runtime delivers it on the next silo at the next period, where the owner
  check runs again. The log event names owner and purpose.
- **Heavy unit:** `UnderSlotAndPermitAsync` waits on the slot semaphore and the permit pipeline with
  the token, so a unit still waiting is released at once; a running unit is cancelled like any
  intent and its permit released in the existing `finally`.

The token is requested only after the budget, not at the start of deactivation, because most
handlers complete within it and the specification's "however long it runs" is worth more than a
fast stop; a host that wants a faster stop shortens `GrainCollectionOptions.DeactivationTimeout`,
which the operations guide names.

*Rejected: cancelling at the start of deactivation.* Every handler in flight on an ordinary deploy
would be cancelled and rerun; the budget exists for exactly this.
*Rejected: a linked token that also carries the caller's cancellation.* Non-Goals.
*Rejected: `IHostApplicationLifetime.ApplicationStopping` as the source.* It fires for the whole
host at once, before the silo's own graceful shutdown has moved activations; the activation's
deactivation is the moment that matters and the runtime already hands it a token.

Evidence: `AggregateGrain.cs:101-135,182-205,243-311`; `HeavyWorkGrain.cs:109-135`;
`TimerOwnerGrain.cs:111-150`; `StoreReaderGrain.cs:96-116` (the shape); `LogEvents.cs:275-310` (the
next id); Orleans `OnDeactivateAsync` and `GrainCollectionOptions.DeactivationTimeout`. Tests: a
two-silo cluster with PostgreSQL reminders and intent store, `DeactivationTimeout = 2 s`, handlers
that await `Task.Delay(Timeout.Infinite, token)`, the first silo stopped with `StopAsync`: the timer
fires on the second silo, the recorded command completes there with attempt count 1, the forwarded
command's caller sees the message; and a fourth handler that ignores the token completes before
the silo returns from `StopAsync` with a budget longer than the handler.

### D2 — The tick unregisters the reminder it fired, not one registered since

`TimerOwnerGrain` keeps a `HashSet<string> _renewed` beside `_firing`. `RegisterAsync`, after it has
registered the reminder under the change gate, adds the name to `_renewed` when `_firing` contains
it — a registration for a purpose and due time whose tick is in flight. `FireAsync`, after the
handler returns, unregisters by name only when `_renewed` does not contain the name, and removes the
name from `_renewed` in the same place where `_firing` is cleared. Because the grain is single-threaded
between awaits and both sets are touched only inside the turn, no gate is needed beyond the existing
one. A registration with a later due time has a different name, is not in `_firing`, and the old name's
unregister removes only the old reminder — the scenario the existing code already handles, kept
as a test so the two cases are proven together. The tick for the renewed reminder that arrives while
the handler still runs is dropped by `_firing` as today; the next one, a retry period later, finds
the reminder due and fires it.

*Rejected: comparing `IGrainReminder` handles.* Orleans documents the handle as not valid beyond an
activation and exposes no etag through it.
*Rejected: a registration nonce in the reminder name.* Changes the name's format, eats into the
purpose's length budget, and makes a re-registration with the same due time a new reminder that the
old name's unregister no longer sees — the same effect with more moving parts.

Evidence: `TimerOwnerGrain.cs:37-61,111-159`. Test: `LongHandlerTimerTests`-shaped, with a handler that
calls `IDurableTimers.RegisterAsync` for its own owner and purpose with `due.DueAt` and returns;
assert the timer is listed after the handler returned and fires again within a retry period — fails
today with an empty list; a second test with `due.DueAt + 2 s` asserting exactly one timer, the
later one.

### D3 — The owner id is validated on every member, against the limit the store's key column gives

`ReminderName` gains `MaxOwnerIdLength` and `EnsureValidOwnerId(string ownerId)`: not empty, not
longer than the limit, with a message naming the limit and the length given, as `EnsureValidPurpose`
does. The limit is the reminder store's 150 characters less what the runtime's string form of the
timer grain's id adds around the key — the grain type's name and its separator — computed once from
the runtime for the timer grain type rather than typed in, and a unit test asserts that a
`GrainId` built for the timer grain with a key of exactly the limit renders to at most 150
characters and one character more does not. `DurableTimers` calls it on `RegisterAsync`, `CancelAsync`,
`CancelAllAsync` and `ListAsync`: an owner that cannot have a timer gets a refusal, not an empty
answer. The limit is enforced whatever reminder service the host runs — in memory has none — so a
test does not pass on a name production refuses. `IDurableTimers` documents the `ArgumentException`
on every member; the process timer owner (`saga:` + the process key) is short and unaffected.

*Rejected: validating in the grain.* The purpose is refused before the call, at the host's side;
the owner id should be refused at the same place, and a grain call with an over-long key may already
be what fails.

Evidence: `TimerOwnerGrain.cs:162-213`; `DurableTimers.cs:8-25`; `SurfaceTrimTests.cs:30,50` (the
purpose tests to mirror); the ADO.NET reminder scripts' column widths; `SagaProcessGrain.cs:231-237`.

### D4 — Documentation

The operations guide gains *Stopping a silo* after *Reminder profile and clocks*: what a stop does
on each path, the deactivation budget as `GrainCollectionOptions.DeactivationTimeout` and its default,
that a handler should pass the token to its awaits and may ignore it at the price of running to its
end, that a stop is logged as `117_114`, and that a hard death has none of this. The sentence at
`operate-the-orleans-execution-model.md:218-220` gains the stop case. Where process timeouts are
written, one sentence says a timeout may reschedule itself with the same due time. The log-events
page gets the row; `IDurableTimers` and `ITimerHandler.OnDueAsync` say what the token means.

Evidence: `docs/guides/operate-the-orleans-execution-model.md:207-221`; `docs/reference/log-events-schema.md`;
`IDurableTimers.cs`.

## Risks / Trade-offs

- [A handler that commits and then observes the token is rerun] → the at-least-once every path
  already promises; the log event makes the rerun attributable; the guide says to observe the token
  before the commit, not after.
- [A forwarded command's caller now sees a failure it did not see before] → before, it saw the
  response timeout after 30 s for the same outcome; the message says what to do.
- [Waiting for running handlers on deactivation delays a silo's stop] → bounded by the runtime's
  budget, which is the host's setting; today the silo stops and the handler runs on unseen.
- [`_renewed` outlives a tick whose handler threw] → both sets are cleared in the same `finally`.
- [The owner-id limit is computed from the runtime and could shift with an Orleans version] → the unit
  test measures the rendered grain id against 150, so a shift fails the test rather than the store.

## Migration Plan

Patch release. No schema change. New public surface: one log event id; `ArgumentException`
documented on every `IDurableTimers` member. A consumer whose owner ids exceed the limit gets the
refusal on upgrade, at registration, where the store would have refused the row. Rollback: none
needed; a token a handler ignores is a token.
