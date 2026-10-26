# Answer a timer tick at once

> **Status:** approved

## Why

On the Orleans execution model a durable timer is a reminder. Today the reminder call stays open until the timer's
handler returns. A handler that runs longer than the runtime's response timeout therefore makes Orleans log
`Could not deliver reminder tick` at Error, with a `TimeoutException`, once per tick it outlasts. The timer itself is
fine: it fires once and runs to its end. Still, the line is an Error the operator has to learn to ignore, so "the
logs are clean" stops working as a check on any host whose timers do real work, for example sending a batch of
mails. Since 4.4.8, store readers and singleton work no longer leave this line. Durable timers were left out on
purpose, and 4.4.8 recorded that as open (*Operate the Orleans execution model*, *Reminder profile and clocks*).

There is a second, quieter problem. A timer handler that throws is not logged by the framework at all. The only
trace is Orleans' own reminder error line, the same line a merely long handler leaves. An operator cannot tell
"slow" from "failed".

## What Changes

The effect a consumer sees: a durable timer's tick is answered at once, whatever its handler does. A handler longer
than the response timeout leaves no reminder error. A handler that fails is logged once by the framework, naming the
owner and the purpose, and the timer fires again after its retry period as it does today.

- **The tick hands the handler over and returns.** The timer's grain receives the tick and starts the handler in a
  call to itself that carries no response, the pattern the aggregate and runner grains already use for long
  commands. That call keeps the grain activated while the handler runs, and the reminder call itself returns at
  once.
- **Unchanged:**
  - a timer still fires once per due time;
  - a tick that arrives while its handler runs still does nothing;
  - the timer is unregistered only after the handler has completed;
  - a handler stopped with its silo leaves the timer registered to fire on the next silo, and that stop is logged
    as before (`117_008`);
  - a handler whose events were committed but not published still counts as fired (`117_129`);
  - re-registering from the handler keeps the new timer;
  - the owner check, the due-time tolerance and the registration limits.
- **A failing handler is logged.** It gets a new Error event, `117_137` `TimerHandlerFailed`, which names the owner
  and the purpose and carries the exception. The timer stays registered and fires again after its retry period.
  This is the same behaviour as today, now visible as the framework's own event.
- **Documentation:** `docs/guides/operate-the-orleans-execution-model.md`:
  - *Reminder profile and clocks* no longer says that a long timer handler leaves the reminder line.
  - *What to watch* drops the exception for timers and routes `117_137` to an alert.
  - `docs/reference/log-events-schema.md` gets the new event.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`: *A keep-alive is answered while its grain is busy* is replaced by *A reminder tick is
  answered while its grain is busy*. The new requirement keeps every guarantee and scenario the old one gave store
  readers and singleton work, and extends the guarantee to durable timers: no reminder error for a long handler, one
  framework event for a failing one, and everything a timer guarantees unchanged. It drops the clause that the
  documentation names the reminder line a long timer handler leaves. The delta is written as REMOVED plus ADDED
  because a MODIFIED delta cannot drop a scenario.

## Impact

- **Code:** `Stratara.Orleans`:
  - `Timers/TimerOwnerGrain.cs`, where the tick starts a one-way self-call that runs the handler;
  - `Timers/ITimerOwnerGrain.cs`, which gains that one-way method under a new alias;
  - `Diagnostics/OrleansLog.cs` and `Stratara.Diagnostics/LogEvents.cs` (`117_137`).

  No public type, member or option changes. The new grain method is only ever called by an activation on itself, so
  a rolling deployment that mixes silos of both versions never sends it across versions.
- **Tests:** `tests/Stratara.Orleans.IntegrationTests/Timers`:
  - `LongHandlerTimerTests` also asserts that a handler outlasting a shortened response timeout leaves no
    `Could not deliver reminder tick`;
  - a new test shows that a handler which throws is logged as `117_137` once per attempt and fires again after the
    retry period.

  The existing timer classes stay green: owner check, hard kill, re-registration, committed but not published,
  reentrancy and the silo stop.
- **Documentation:** as above, plus `CHANGELOG.md`.
- **Release:** a patch, with `name-the-projections-once` if both are ready.
- **Superseded sources:** the guide's paragraph saying that a long timer handler is logged at Error by the runtime,
  and the `orleans-execution` scenario *The documentation names the reminder line a durable timer still leaves*.
