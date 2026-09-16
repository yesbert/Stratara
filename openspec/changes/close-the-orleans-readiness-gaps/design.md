## Context

The three defects and the owner's decision on heavy commands are in `proposal.md`. The current code:

- **Drain.** `src/Stratara.Orleans/Singleton/OutboxDrainWork.cs:28-63` resumes recorded commands only if
  `OrleansCommandDispatcher` resolves on the silo running it; otherwise it hands stored commands to
  the registered `ICommandOutboxDispatcher`. The intent store writes its records to the outbox table
  with `DataTypeName = typeof(CommandEnvelope).GetQualifiedTypeName()`
  (`src/Stratara.Orleans.EntityFrameworkCore/Intents/CommandIntentStore.cs:21,111`), which is exactly
  the kind the bus drain reads for commands. The guide registers the dispatcher on the API host and the
  drain on the silos (`docs/guides/migrate-to-the-orleans-execution-model.md:90,92`).
- **Heavy work.** `IntentHandOver` sends a heavy command to `HeavyWorkGrain`, which runs the whole unit
  inside the call (`OrleansCommandDispatcher.cs:66-74`, `HeavyWorkGrain.cs:89-120`) and enters the
  aggregate's turn marker there, so the mediator behaviour does not route it back to the aggregate
  (`AggregateGrain.cs:291`, `AggregateGrainBehavior.cs:35`). The dispatcher and the resumer both put
  every hand-over into `AggregateSendLane` under the aggregate id (`OrleansCommandDispatcher.cs:36,104`);
  the lane releases a key only when the call completes, which for heavy work is when the unit ends.
  The design comment on `HeavyWorkGrain` states the intent: "a long unit does not hold an aggregate's turn".
- **Timers.** `TimerOwnerGrain` is `[Reentrant]` (`Timers/TimerOwnerGrain.cs:21`). `ReceiveReminder`
  checks the owner and the due time, awaits the handler, and only then unregisters the reminder
  (`:105-127`). The reminder ticks at `DurableTimerOptions.RetryPeriod` (1 minute).

## Goals / Non-Goals

**Goals:**
- No recorded command reaches a bus drain, in any composition, and recorded commands are resumed on the
  silos however the hosts are split.
- The specification describes heavy work as it is built, and heavy hand-overs block nothing.
- A timer's handler runs once per registration, whatever its duration.

**Non-Goals:**
- Running heavy commands inside the aggregate's turn (owner decision 2026-09-16).
- The portable-reader findings, the rebuild pause, saga retries and intent bookkeeping named in the
  proposal.
- A second firing after the timer's activation is lost mid-handler: the handler did not complete, and
  the registration still exists, so firing again is the durable-timer promise working.

## Decisions

### D1 — Recorded commands get a stored kind of their own

The intent store writes records under a kind that names the execution model's record, not the bus
command envelope. The bus drain selects by kind, so it never reads one. The intent store's reads —
due, claim, keep, count — accept the new kind and the 4.1.0 kind, so records still in storage at an
upgrade are resumed.

*Rejected: deciding only by what the drain's silo registers* — the fix the review suggested. It
removes the crossed wiring the guide shows, but a silo without the intent store would still publish
records an API host wrote, and nothing in one process can see how another is composed.
*Rejected: a marker column* — a schema change and a migration for every host, for a distinction the
existing kind column already expresses.

Evidence: `CommandIntentStore.cs:21,111`; `OutboxRepository.cs:67` (the bus side selects by kind).

### D2 — The drain resumes wherever an intent store is registered

`OutboxDrainWork` resumes recorded commands when an `ICommandIntentStore` is registered on its silo —
through an internal resumer it can build from the store and the options, not through the dispatcher
— and additionally drains bus commands and bundles as today. A silo registered with the drain but no
intent store drains bus kinds only; if it finds records of the execution model's kind, it logs a
warning naming `AddStrataraIntentStore` on each period that finds them, so the miswiring is visible.
The guide's per-role table registers `AddStrataraIntentStore` on the silos that run the drain.

*Rejected: failing at start without an intent store* — a host that adopts the execution model for
projections only runs the drain for the bus, legitimately.

Evidence: `OutboxDrainWork.cs:28-63`; `IntentStoreStartupCheck.cs` (existing start check for hosts
with the dispatcher).

### D3 — Heavy hand-overs leave the per-aggregate send order

The dispatcher and the resumer key a heavy hand-over by its intent id instead of its aggregate id, so
it neither waits for earlier calls to its aggregate nor holds later ones. The heavy unit keeps
entering the aggregate's turn marker, so commands its handler dispatches for the same aggregate still
run in place. The single-writer and order requirements name heavy commands as the exception; a
conflict is refused by the version constraint and resumed as a failure.

*Rejected: returning from the heavy hand-over on acceptance* — the review's suggestion. The worker pool
bounds concurrency by holding the call for the unit; returning early would move the unit out of the
bounded turn or require a second queue. Taking heavy work out of the order removes the stall without
touching the bound.

Evidence: `AggregateSendLane.cs` (release on call completion), `OrleansCommandDispatcher.cs:36,104`,
`HeavyWorkGrain.cs:55-66` (design intent).

### D4 — A tick does nothing while the same timer's handler runs

`TimerOwnerGrain` keeps the names of the reminders whose handler is running in the activation. A tick
for a name already in that set returns at once. Before calling the handler, a tick confirms the
reminder is still registered, so a timer cancelled while an earlier tick waited on the owner check is
not fired. The set is cleared in a `finally`, so a handler that throws is retried by the next tick as
today. The grain stays reentrant; its reason — handlers that change their owner's timers — is unchanged.

*Rejected: making the grain non-reentrant* — the documented deadlock it avoids is a handler that
cancels or registers its own owner's timers (`TimerOwnerGrain.cs:15-18`).
*Rejected: unregistering before the handler* — a kill between the unregister and the handler loses
the timer, which the kill scenarios forbid.

Evidence: `TimerOwnerGrain.cs:15-30,105-127`; `orleans-execution` → *The silo is killed with timers open*.

## Risks / Trade-offs

- [A silo runs the drain without an intent store while API hosts record commands] → records are no
  longer published but also not resumed; the warning of D2 names the missing registration, and the
  guide registers it on the silos.
- [A 4.1.0 bus drain still runs somewhere during an upgrade] → it can still read records of the old
  kind; the guide says to stop bus drains before hosts record through the execution model, as it
  already says for adoption, and repeats it for the upgrade.
- [Heavy commands conflict with interactive commands on busy aggregates] → the conflict is refused and
  resumed within the attempt bound, as on the bus; the operations guide says heavy commands should not
  target aggregates that take a steady stream of interactive commands.
- [An in-flight set is per activation] → a lost activation forgets it; the handler of that activation
  did not complete, and the next tick firing it is the promise, not a double.

## Migration Plan

Patch release. No schema change. Records written under 4.1.0 are resumed by the upgraded drain. Stop
any bus outbox worker before upgrading hosts that record commands. Rollback to 4.1.x: records written
under the new kind are not resumed by an older drain; roll back only after the intent store is empty.
