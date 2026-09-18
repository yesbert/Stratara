## Context

A recorded command lives as an `OutboxEntry` row written by `CommandIntentStore.RecordAsync`
(`src/Stratara.Orleans.EntityFrameworkCore/Intents/CommandIntentStore.cs:23-38`, `Timestamp =
DateTimeOffset.UtcNow` at :33, `AttemptCount` 0). The dispatcher starts the record and the lane's
send at once for every dispatch (`OrleansCommandDispatcher.cs:52-54`); the record serializes and opens a
context before it stamps (`IntentRecorder.cs:26-29`). The resumer (`IntentResumer`) reads due rows
ordered by `Timestamp` only (`CommandIntentStore.cs:48`), keeps a row when `AttemptCount >=
MaxDeliveryAttempts` (`OrleansCommandDispatcher.cs:188-193`), and claims the rest in one statement that
stamps `LastHandedOverAt` with the millisecond-truncated `now` and adds one attempt, then reads back
the rows carrying that stamp (`CommandIntentStore.cs:95-121`). The receiving grain starts an
`IntentLease` whose first renewal is unconditional (`IntentLease.cs:43-60`); a failure is recorded with
`RecordFailureAsync`; completion deletes the row (`IntentCompletionQueue`). `CommandRunnerGrain`
(`AggregateGrain.cs:302-312`) runs whatever it is handed; the aggregate and heavy grains refuse only an
intent they hold at that moment.

## Goals / Non-Goals

**Goals:** one run per resumption however claimers race; the dispatch order of a scope survives
recording; the bus's two bounds, counted the bus's way; a stop costs no attempt; one clock.

**Non-Goals:** making the record atomic with the caller's unit of work (documented operating limit);
signing the aggregate id beside the envelope (`keep-a-stop-and-a-resume-honest` D1); changing the
claim's millisecond stamp (D1).

## Decisions

### D1 — Fence the resumed hand-over at the receiver, keep the claim's stamp

**Decision.** The resumption puts the claim's stamp on the internal hand-over envelope. A receiver that
gets a stamped hand-over starts its lease with `TryRenewFromAsync(id, claimedAt, now)`: an update of
`LastHandedOverAt` from exactly `claimedAt` to a later value (at least one millisecond later, so a
renewal in the same millisecond still moves it). Zero rows touched means another runner already renewed
it or the command completed and its row is gone: the grain drops the hand-over, logs it (new event
`IntentHandOverDropped`, Debug) and answers as if accepted. An unstamped hand-over — the dispatch's own,
or one from a 4.1.x silo during a rolling upgrade — starts as today.

**Why this closes the race D3 left open.** Both claimers of one millisecond hand over with the same
stamp; the first receiver's renewal moves the stamp, so the second's compare fails. A hand-over arriving
after completion finds no row. The default interface implementation returns `true` after
`RenewAsync`, so an intent store outside the framework keeps today's behaviour.

**Why not a claim token column.** It is a column for every consumer to migrate, and the stamp that is
already there does the job once the receiver compares it. (`keep-a-stop-and-a-resume-honest` D3 refused
the column for the same reason; its safety argument was what did not hold.)

**Why not below the millisecond.** Tried and removed in D3 of that change: a provider that keeps
milliseconds matches no row.

### D2 — The dispatch order is taken before anything is awaited

**Decision.** `EnqueueCommandAsync` takes `recordedAt` from the registered `TimeProvider` before it
starts the record, and makes it strictly greater — by at least one millisecond — than the last one the
same scope took for the same lane key (the dispatcher is scoped, so a field holds it). It passes it to
`ICommandIntentStore.RecordAsync(…, recordedAt, …)`, whose default implementation calls the existing
overload. `GetDueAsync` orders by `Timestamp`, then `Id`.

**Why a millisecond.** Every provider the framework supports keeps at least milliseconds; a smaller
step could collapse to a tie after the store's rounding.

**Why not a sequence column.** Exact, but a migration and a store-specific identity for an order that
only has to hold within one scope.

### D3 — Count the first hand-over; count conflicts apart

**Decision.** The resumer keeps a row when `AttemptCount + 1 >= MaxDeliveryAttempts` — the first
hand-over is attempt one — or when `ConflictCount >= MaxConflictRequeues`. A failure that is a
concurrency conflict (`ConcurrencyConflictException`, `ConcurrencyException`,
`DbUpdateConcurrencyException`, after the in-process conflict pipeline has retried) is recorded with
`RecordConflictAsync`, which adds one to `ConflictCount` and takes one off `AttemptCount` (not below 0),
so the delivery bound measures genuine failures only, as on the bus. Default implementation: calls
`RecordFailureAsync`, which keeps today's counting for a store outside the framework.

**Why a column.** The count must survive the silo, as the attempt count does; there is no other place
it can live. The owner chose it over documenting the difference (2026-09-18).

### D4 — A stop gives its attempt back

**Decision.** Where a recorded command's handler is cancelled because its silo stops
(`CommandExecution`, the `stopping` branch), the lease calls `ReturnAttemptAsync(id)` before it ends —
one attempt off, not below 0. Default implementation: nothing. The existing test
`StoppingSiloTests` asserts the attempt count it deliberately left out until now.

## Risks / Trade-offs

- [A 4.1.x record resumed by 4.2.0 runs one attempt fewer than 4.1.x would have run it] → stated in
  the CHANGELOG; it is the bus's count.
- [A 4.1.x silo during a rolling upgrade hands over unstamped] → such hand-overs are not fenced, as
  today; the fence applies once every silo is on 4.2.0.
- [A receiver whose fencing renewal fails for a store error, not a moved stamp] → the hand-over is not
  dropped: only "zero rows" drops it; an exception fails the hand-over as today and the drain resumes
  it after the grace.

## Migration Plan

Consumers add a migration for the new column (`dotnet ef migrations add`), as the upgrade note in the
migration guide says; the column defaults to 0, so rows in flight need nothing. Rollback: a 4.1.x silo
ignores the column.

## Decisions taken during implementation (2026-09-18)

- **D3, amended — the conflict bound is the bus's, to the run.** A command is kept when
  `ConflictCount > MaxConflictRequeues`, not `>=`: the bus's `MessageRetryPolicy` dead-letters a conflict
  when the delivery attempt exceeds the bound, which is `MaxConflictRequeues + 1` runs, and the spec says
  a contended command is kept no sooner than on the bus. Evidence: `ResumedOnceInOrderTests` (bound 3,
  kept after 4 runs).
- **D3, amended — the first hand-over counts where the record shows it ran.** The attempts are
  `AttemptCount + 1` where the record carries a claim (`AttemptCount > 0`) or a genuine failure
  (`LastFailure` set and no conflict), and `AttemptCount` otherwise. For every `MaxDeliveryAttempts >= 2`
  this is the design's `AttemptCount + 1 >= Max`; it differs only at a bound of 1, where the design's rule
  would keep every due record without running it once — breaking *The host dies after acceptance* — and
  would also keep a command whose only run was stopped or met a conflict. Evidence:
  `ResumeOnceInOrderTests`.
- **No migration file.** The repository ships no EF migrations; consumers generate their own, which the
  migration guide's 4.2 section says. `StoreSchemaAdditionsTests` asserts the column.
- **The own-clock and out-of-order scenarios are verified without a kill.** Orleans cannot run on a
  shifted `TimeProvider` (its activation collector throws), so the own-clock test runs the dispatcher
  and the resumer on PostgreSQL with a recording grain factory; the out-of-order test holds the records
  with an active replay instead of a kill, which leaves them in the same state.
