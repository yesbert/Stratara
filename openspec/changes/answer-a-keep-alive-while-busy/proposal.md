# Answer a keep-alive while busy

> **Status:** approved

## Why

On the Orleans execution model every store reader and every singleton work keeps a keep-alive reminder.
The reminder brings the grain back after a silo is lost. While a store reader catches up a backlog that
takes longer than the runtime's response timeout, the tick waits behind the catch-up. The runtime then
logs `Could not deliver reminder tick` at Error, with a `TimeoutException`, once per partition and period.
The same happens to a singleton work whose run outlasts the response timeout. Nothing is lost. The reader
keeps reading and the next tick is delivered. But a large catch-up leaves dozens of errors on the silo, so
"the logs are clean" stops working as a check after a burst. This is the class of defect that F-024 removed
for long hand-overs.

A consumer reported it on 2026-10-23 under 4.4.7 (consumer finding F-028). Removing 10,000 entries let one
projection catch up for about two minutes. Its partitions logged the error 32 times in one minute.

## What Changes

The effect a consumer sees: a keep-alive tick is answered within the response timeout, whatever its grain is
doing. A catch-up or a singleton run longer than the timeout leaves no reminder error in the silo's log.

- **Store readers (projections and sagas):** the keep-alive tick and the poll request a catch-up and return at
  once. They do not wait for the catch-up to end, which is what a nudge already does. A catch-up still runs
  one at a time per partition, and a tick or a poll during one makes it read once more before it ends. The
  keep-alive tick interleaves with any other call to the reader, so it waits for nothing.
- **A starting silo:** a silo that starts makes sure every store reader and every singleton work runs, and it
  waits for that call. That call now interleaves too. Before, a silo starting while another silo's singleton
  run or a reader's catch-up outlasted the response timeout waited behind it and received a timeout in its own
  start.
- **Singleton work:** the keep-alive tick interleaves with a running run. All it does is make sure the work's
  period is armed, which it does at once. A run still never overlaps with another run of the same work or
  with a call to its grain, so the guarantee of one run at a time holds.
- **Unchanged:** commit order and checkpoints. Also unchanged: one catch-up per partition, pauses and their
  leases, rebuilds and replays, waiting for a projection, retirement of readers the host no longer runs, the
  failover of singleton work, and every log event of the framework's own.
- **Durable timers are not changed.** A timer handler that outlasts the response timeout still holds its
  reminder call open, because that open call is what keeps the timer's owner activated until the handler
  ends. The runtime logs it as above. The operating guide will say so where it already describes such a
  handler. Moving durable timers to the same shape is a separate change.
- **Documentation:** `docs/guides/operate-the-orleans-execution-model.md` gets two additions:
  - *Reminder profile and clocks*: a store reader or a singleton work answers its keep-alive while it reads or
    runs. A timer handler that outlasts the reminder call's response timeout makes the runtime log
    `Could not deliver reminder tick` at Error for that tick, and that line says nothing about the timer.
  - *What to watch*: Orleans' `Could not deliver reminder tick` for a store reader or a singleton work is now
    worth an alert. It no longer comes from a long catch-up.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`: gains *A keep-alive is answered while its grain is busy*. A store reader that catches up
  longer than the response timeout, and a singleton work that runs longer than it, leave no reminder delivery
  error in the silo's log, and a silo that starts meanwhile is not held up by them. The documentation says that
  a durable timer's long handler still does.

## Impact

- **Code:** `Stratara.Orleans`. These are the store reader base grain, whose keep-alive tick and poll stop
  waiting for the catch-up and whose keep-alive tick and start call interleave, and the singleton work grain,
  whose keep-alive tick and start call interleave. No public type, member or option changes. Grain interfaces and their aliases
  stay as they are, so a rolling deployment mixes silos of both versions without trouble.
- **Tests:** `tests/Stratara.Orleans.IntegrationTests`. They add a projection catch-up and a singleton work
  run longer than a shortened response timeout, with the silo's log checked for reminder delivery errors, and a
  second silo that starts during such a run.
  `LongHandOverTests` uses the same pattern.
- **Documentation:** `docs/guides/operate-the-orleans-execution-model.md`, as above, and `CHANGELOG.md`.
- **Release:** a patch. No API is added and behaviour only gets quieter.
- **Consumer finding:** NextPA `stratara-findings.md`, F-028.
- **Superseded sources:** none.
