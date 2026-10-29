# Run a conflicting command again at once

> **Status:** approved

## Why

On the Orleans execution model, a recorded command whose handler fails on a concurrency conflict is recorded with
the conflict and then waits. The drain resumes it only once its hand-over has lapsed, after
`OrleansDispatchOptions.IntentGrace`, 30 seconds by default. On the bus a conflicting message is redelivered at once.
A change that collides with background work on the same stream therefore takes half a minute instead of a moment.

A consumer reported it on 2026-10-28 under 4.5.0 (consumer finding F-031). A person deleted an entry while its
processing appended to the same stream. The delete met a conflict at 20:32:12.664 and ran again at 20:32:43, and two
browser tests that wait ten seconds for the deletion fail since the move to the Orleans model.

Verified on `main` at `f76fa6d` (4.6.0). `CommandExecution.RunIntentAsync` records the conflict through the lease and
ends the lease. The drain's due query asks for records whose last hand-over *and* record time are older than the
grace, so a command recorded moments before its conflict waits for the full grace, wherever its hand-over stamp
stands.

## What Changes

The effect a consumer sees: a command whose attempt meets a concurrency conflict runs again within moments and, once
the competing write has committed, succeeds. A command that keeps conflicting is still bounded and kept as before.

- **A conflict runs the command again at once, in a fresh unit of work.** The silo that ran the attempt holds the
  command's hand-over, pauses briefly as the framework's concurrency-conflict pipeline
  (`ResilienceNames.ConcurrencyConflict`, five retries from 50 ms, exponential with jitter) does, and runs the handler
  again in a new scope. A heavy command keeps its permit meanwhile.
- **The conflict bound still holds.** Every conflict is recorded and counted as before. The command runs again only
  while its recorded conflicts leave room under `MessageRetryOptions.MaxConflictRequeues`. Once the pipeline's
  retries are used up, the drain resumes it after the grace as before. A command that conflicts on every attempt runs
  as often as before and is kept with the conflict.
- **`ICommandIntentStore.CountConflictAsync`** records a conflict as `RecordConflictAsync` does and answers how many
  conflicts the command has met. The default answers `null`, and a command on a store that cannot tell is left to the
  drain, as before. The framework's store implements it.
- **Unchanged:** failures other than conflicts, the delivery bound, the drain, hand-over fencing, a forwarded command
  that is not recorded (its conflict goes back to its caller), and the bus path.
- **Documentation:** `docs/guides/operate-the-orleans-execution-model.md`, in the section on kept commands, says what
  a conflict does on the execution model.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`: gains *A recorded command that meets a concurrency conflict runs again at once*.

## Impact

- **Code:** `Stratara.Orleans` (the command execution and the intent lease), `Stratara.Abstractions` (one default
  interface member on `ICommandIntentStore`), and `Stratara.Orleans.EntityFrameworkCore` (the store implements it).
  No grain interface and no stored form changes.
- **Tests:** `tests/Stratara.Orleans.IntegrationTests` on the PostgreSQL store, covering a command whose first attempt
  conflicts, the conflict bound and a heavy command. A unit test in `tests/Stratara.Orleans.Tests` covers the store
  default.
- **Documentation:** `docs/guides/operate-the-orleans-execution-model.md` and `CHANGELOG.md`.
- **Release:** a patch (4.6.1), together with `wake-only-the-readers-a-commit-concerns`. The API addition is additive.
- **Consumer finding:** NextPA `stratara-findings.md`, F-031.
- **Superseded sources:** none.
