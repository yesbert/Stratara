# Carry cancellation to grain handlers and harden timer registration

> **Status:** approved (owner, 2026-09-17 — recorded at the owner's request)

## Why

The round-5 audit left four low findings on the grain paths that share one root and one file:

- **No handler on a grain path is ever told to stop (R5-Cmd-011, R5-Tim-006).** The aggregate grain,
  the runner grain, the heavy-work pool and the timer owner all invoke the consumer's handler with
  `CancellationToken.None`. When a silo stops, the runtime gives each activation the deactivation
  timeout — 30 s by default — and then abandons it; a handler still running at that point keeps
  running in a process that is going away, sees no signal, and cannot finish cleanly. For a timer that
  means the reminder is never unregistered — it fires again on the next silo while the first run may
  still be half-way, and nothing distinguishes that from the at-least-once retry the contract
  promises. For a recorded command it means the lease stops renewing only when the process dies, and
  the drain resumes the command after the grace beside a first run that was never stopped. For a
  forwarded command the caller sees the response timeout, not the stop.
- **Re-registering a timer for the same purpose while its handler runs loses the new timer
  (R5-Tim-007).** A registration first cancels every reminder of the purpose and then registers the new
  one; when the due time is the same, the new reminder has the name of the one firing, and the tick
  unregisters "its" reminder by name once the handler returns — which is now the new one. A process
  that reschedules its own timeout from the timeout handler with the same due time ends without a
  timeout.
- **The owner id is not checked against what the reminder table holds (R5-Tim-008).** A purpose
  longer than 130 characters is refused with a message naming the limit; an owner id of any length is
  accepted, becomes the timer grain's key, and fails — or is truncated — in the reminder table's
  150-character grain-id column, at the first tick and far from the registration.

## What Changes

- **A stopping silo tells every running handler.** Each grain that runs a consumer's handler — the
  aggregate's activation, the runner for commands without an aggregate, the heavy-work pool and the
  timer owner — passes a cancellation token that is requested when the activation is deactivated and
  its running handlers have not completed within the runtime's deactivation budget. A recorded
  command whose handler stops on it is resumed after the grace on another silo, as after a crash, and
  the stop counts no attempt. A forwarded command whose handler stops on it fails back to its caller
  with a message saying the silo stopped and that the command may be dispatched again. A timer whose
  handler stops on it stays registered and fires on the next silo. A handler that does not observe
  the token runs to its end, and the activation waits for it as long as the runtime allows. Every
  stop is logged with the handler's identity.
- **A timer registered while its own tick runs is kept.** The tick unregisters the reminder it
  fired, not a reminder registered since — whatever the new due time is.
- **The owner id is validated on registration, cancellation and listing**, against the length the
  reminder store holds for a grain key, with a message naming the limit, the way a purpose is.
- **Consumer-visible effects:** handlers on the grain paths receive a token that is no longer
  `None`; a caller of a forwarded command can see a new failure message on a stopping silo; an
  over-long owner id is refused where it was accepted; a re-registered timer fires where it was lost.
  One new log event. Versioning: patch. Closes R5-Cmd-011, R5-Tim-006, R5-Tim-007, R5-Tim-008.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`:
  - *An accepted command is recorded before the call returns and resumed after a crash* — a handler
    on this path receives a cancellation requested by a stopping silo; what follows for a recorded and
    a forwarded command; a handler that ignores it runs to its end; new scenarios.
  - *Work that must happen once happens once per cluster* — a timer's handler receives the same
    cancellation and the timer then fires on the next silo; a timer re-registered during its tick is
    kept; an over-long owner id is refused on registration with a message naming the limit; new
    scenarios.

## Impact

- `Stratara.Orleans` — `AggregateGrain` (a stopping token per activation, waited for on
  deactivation, passed through `CommandExecution` to the handler invoker), `CommandRunnerGrain`,
  `HeavyWorkGrain`, `TimerOwnerGrain` (the token, the wait on deactivation, the re-registration mark),
  `ReminderName` (owner-id limit), `DurableTimers` (validation on every member), `OrleansLog`.
- `Stratara.Abstractions` — `IDurableTimers` documentation of the owner-id limit and the token's
  meaning on `ITimerHandler.OnDueAsync`.
- `Stratara.Diagnostics` — one log event id in `LogEvents.Orleans`: `117_008`, Information.
- As implemented, also: `src/Stratara.Orleans/Hosting/SiloStopSignal.cs` (the stop token's source, registered by
  `AddStrataraOrleans`, `AddStrataraAggregateGrains` and `AddStrataraDurableTimers`); tests
  `tests/Stratara.Orleans.IntegrationTests/Hosting/StoppingSiloTests.cs`, `Timers/ReRegisteredTimerTests.cs`,
  `tests/Stratara.Orleans.Tests/SurfaceTrimTests.cs`.
- `docs/guides/operate-the-orleans-execution-model.md` (a *Stopping a silo* section: the token, the
  deactivation timeout as the host's setting, what a handler should do), `docs/guides/write-a-saga.md`
  or wherever process timeouts are written (the re-registration case), `docs/reference/log-events-schema.md`,
  `src/Stratara.Orleans/README.md`, `CHANGELOG.md`, `llms.txt`.
- Tests: a timer re-registered from its own handler with the same due time; an over-long owner id;
  a silo stopped while a timer's handler, a recorded command's handler and a forwarded command's
  handler wait on the token; a handler that ignores the token; documentation tests.
- Versioning: patch.
