# Close the Orleans readiness gaps

> **Status:** approved (owner, 2026-09-16)

## Why

A review of the Orleans execution model on 2026-09-16, before application teams build on it, found
three defects that reach every deployment regardless of its store and its commit-order reader:

- A silo composed as the migration guide shows can publish recorded commands to the bus. The outbox
  drain decides whether it resumes recorded commands or publishes stored commands by whether the
  execution model's dispatcher is registered *on the silo running it*; the guide registers the
  dispatcher on the API host and the drain on the silos. Recorded commands and bus commands are stored
  under the same kind, so such a silo hands every recorded command — kept ones included — to the bus
  and deletes it: a command runs twice, a kept command comes back, the attempt bound does not hold.
- Heavy commands run outside their aggregate's activation, deliberately, so that a long unit does not
  hold the aggregate's turn. The specification promises without exception that two commands naming
  one aggregate never run concurrently and that commands keep their aggregate's order, so it promises
  what the implementation was designed not to do. The same design leaves a stall behind: a heavy
  hand-over holds the per-aggregate send order until the unit has finished, so two due heavy commands
  on one aggregate hold up the drain's resume pass for the length of the first.
- A durable timer can fire twice. A timer handler that runs longer than the retry period is started a
  second time by the next tick, because nothing records that it is already running and the timer is
  only removed after the handler.

## What Changes

- **Recorded commands and bus commands are told apart in storage.** A command recorded by the
  execution model is stored under a kind of its own, so no bus drain ever reads one, and the drain
  resumes recorded commands wherever an intent store is registered on its silo, whichever host
  dispatched them. The migration guide registers the intent store on the silos that run the drain.
- **Heavy commands are exempt from the single-writer and order promises, and the specification says
  so.** They keep their own bounded pool and do not take the aggregate's turn; a conflict with a
  command on the same aggregate is refused by the store's version check and resumed like any failure,
  as on the bus. Heavy hand-overs no longer hold the per-aggregate send order, so they neither wait for
  nor stall the commands and resumptions around them.
- **A timer fires once however long its handler runs.** A tick that arrives while the same timer's
  handler runs does nothing, and a tick checks that its timer is still registered before it calls the
  handler.
- **Consumer-visible effects:**
  - A heavy command dispatched from a scope no longer waits for an earlier command to the same aggregate
    from that scope, and the next command no longer waits for it. Two heavy commands to one aggregate
    from one scope are not ordered.
  - A command recorded under 4.1.0 and still in storage when a host upgrades is resumed by the upgraded
    drain; the guide says to let a bus drain run nowhere while such records exist.
- No schema change, no migration, no new public type or member.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`:
  - *One aggregate has one writer across the cluster* — heavy commands are named as the exception.
  - *An accepted command is recorded before the call returns and resumed after a crash* — heavy
    commands are exempt from the order promises; new scenario: the drain runs on a silo that did not
    dispatch the commands.
  - *Work that must happen once happens once per cluster* — new scenario: a timer handler runs longer
    than the retry period.

## Impact

- `Stratara.Orleans.EntityFrameworkCore` — the intent store's stored kind; reading records written
  under the previous kind.
- `Stratara.Orleans` — the outbox drain's choice of path; the heavy hand-over's place in the send order;
  the timer owner's tick.
- `docs/guides/migrate-to-the-orleans-execution-model.md` (registrations per role, the upgrade note),
  `docs/guides/operate-the-orleans-execution-model.md` (heavy work and timers),
  `docs/concepts/orleans-execution-model.md` (one writer per aggregate).
- Tests: a drain on a silo without the dispatcher; a heavy command beside a normal command on one
  aggregate and two due heavy intents on one aggregate; a timer whose handler outlasts the retry period.
- Versioning: patch.
- Not in scope, found by the same review and left for their own change: the portable counter reader's
  gaps (the interceptor registered by hand, a smaller partition count, the interceptor's transaction on
  a failed save); a rebuild's pause kept only in memory; saga retries repeating stateless sagas; the
  intent attempt count and the ordering of due intents.
