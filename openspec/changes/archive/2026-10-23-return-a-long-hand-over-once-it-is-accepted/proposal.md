# Return a long hand-over once it is accepted

> **Status:** approved

## Why

On the Orleans execution model the hand-over of a heavy command, and of a command that names no aggregate,
is a grain call that stays open until the unit has run. A unit longer than the runtime's response timeout
therefore ends that call with a timeout. The operations guide says this timeout "is not logged". That holds
for the framework's own log line, which the dispatcher suppresses. It does not hold for the Orleans runtime,
which logs on both ends: *Response did not arrive on time* on the dispatching host, and three to five
*Dropping expired message* warnings on the silo. The unit itself runs to its end and commits. Nothing is lost
or run twice, but every long unit leaves warnings that look like a messaging fault, and "the logs are clean"
stops working as a check.

A consumer reported it on 2026-10-22 (consumer finding F-024), together with a second documentation defect
in the same area (consumer finding F-023). The migration guide says Orleans' PostgreSQL scripts ship inside
the `Microsoft.Orleans.Clustering.AdoNet` and `Microsoft.Orleans.Reminders.AdoNet` packages. They do not.
The 10.3.1 packages carry the assembly, its documentation, a README and a logo. The repository's own
scenario-test README already says so.

## What Changes

The consumer-visible effect is that a heavy command, or a command that names no aggregate, is answered as
handed over as soon as the silo has accepted it, however long it then runs. No grain call that a host waits
on stays open for the length of the unit, so neither the dispatching host nor the silo logs a timeout for
one. The aggregate grain already runs its order this way.

- **Heavy work:** the heavy-work pool answers the hand-over once it holds the unit and the intent's lease
  has started. The unit then runs on the silo's workers under its permit, as today. Until the unit ends,
  the pool's activation is kept alive by a call the grain makes to itself, the shape the aggregate grain
  uses for its order.
- **A command that names no aggregate:** its runner answers the hand-over once the intent's lease has
  started, and runs the handler through a one-way call to itself.
- **The dispatcher and the resumption** log a hand-over that fails or times out the same way for every kind
  of command. The exception that kept a long hand-over's timeout quiet goes away, because a hand-over no
  longer spans the unit. A timeout on a hand-over now means the silo did not accept it in time, which is
  worth seeing.
- **Unchanged:** completion, failure recording, resumption after a crash, the permit bound, cancellation
  when a silo stops, and the forwarded command, which still waits for its handler and still observes a
  timeout when that handler outlasts it. The intent store stays the only place a caller learns how a
  recorded command ended. No caller waited for a hand-over's end before this change either.
- **Documentation:**
  - `docs/guides/operate-the-orleans-execution-model.md`, *The response timeout*: replace the sentence that
    says a long hand-over's timeout is not logged. A hand-over returns on acceptance and is not bounded by
    the timeout. Only a forwarded command is.
  - `docs/guides/migrate-to-the-orleans-execution-model.md`, *Register the silo*: say that the ADO.NET
    provider packages ship no scripts. The scripts come from the Orleans repository at the release tag,
    under `src/AdoNet`, and the host carries its own copy.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`: gains *A hand-over answers once the command is accepted*. A heavy command or one
  that names no aggregate leaves no call open for the length of its run, and the runtime logs no timeout
  for it on either end.

## Impact

- `src/Stratara.Orleans/Aggregates/HeavyWorkGrain.cs`: `ExecuteIntentAsync` returns after acceptance. A new
  one-way, interleaving self-call holds the activation while units are in flight.
- `src/Stratara.Orleans/Aggregates/AggregateGrain.cs`: `CommandRunnerGrain` starts the lease on the call,
  then runs through a one-way self-call. `ICommandRunnerGrain` gains that one-way method.
- `src/Stratara.Orleans/Aggregates/OrleansCommandDispatcher.cs`: `IntentHandOver.Observe` drops the
  `spansTheUnit` exception and its XML remark.
- `docs/guides/operate-the-orleans-execution-model.md`, `docs/guides/migrate-to-the-orleans-execution-model.md`,
  `docs/concepts/orleans-execution-model.md` (checked; changed only if it repeats either claim), `CHANGELOG.md`.
- No public API, package, dependency, tier or schema change, and no migration. A patch release (4.4.7).
- Rolling upgrade: the grain methods keep their names and aliases. A dispatcher of this release calling a
  silo of the previous one sees the previous behaviour, including its warnings. A dispatcher of the previous
  release calling a silo of this one is answered sooner. Neither waits for anything the other does not
  provide (`design.md`, *Migration Plan*).
- Dissolved or superseded legacy sources: none.
