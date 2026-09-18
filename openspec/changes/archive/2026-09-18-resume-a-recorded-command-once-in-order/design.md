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
- **D3, amended — the record counts the dispatch's hand-over.** The store writes a record with
  `AttemptCount = 1` (the overload with `recordedAt`), and the resumer keeps it when
  `AttemptCount >= MaxDeliveryAttempts`, instead of presuming the first hand-over with `AttemptCount + 1`. A
  presumption cannot be given back: a stop or a conflict on the dispatch's run had nothing to return, and the
  presumed attempt was charged anyway once a resumption followed (a stop, then two failures, kept at a bound of 3),
  while a record whose failure an operator's return left in place gained a phantom attempt. Counted in the record,
  the stop and the conflict give back the dispatch's attempt like any other. A host that dies before the hand-over
  has used the attempt, as a bus delivery to a consumer that crashes has; under a bound of 1 such a command is kept
  for an operator without running — kept, not lost. A 4.1.x record, written with 0, runs as 4.1 ran it. The
  operator's return clears `last_failure` and `conflict_count` too. The interface default of the `recordedAt`
  overload records through the old overload, so a store outside the framework counts from zero as before.
  Evidence: `ResumedOnceInOrderTests` (the outcome sequences and the operator's return at bounds 1 and 3).
- **D3, amended — a conflict is what the bus counts as one.** The bus transports treat only the event store's
  `ConcurrencyException` as a conflict (`ConcurrencyConflictException` and EF Core's
  `DbUpdateConcurrencyException` are failures there, and neither derives from it); the resumed path now does the same
  instead of the three types D3 listed.
- **A stop gives back the attempts of the commands it gives up.** An aggregate's activation that abandons its queue
  on a stop returns the attempt of every recorded command still queued, not only the running one's (D4).
- **A dropped hand-over stops holding its command at once.** The aggregate's activation releases a hand-over whose
  lease was dropped or failed as soon as its lease answers, and a queued entry releases the command only while it
  still holds it, so a later valid hand-over is judged by its own lease.
- **A late dispatch hand-over checks that its command is still there.** The unstamped hand-over's renewal at the start
  — only where the record time no longer holds it — uses a new `TryRenewAsync` (default: renew and run) that reports
  whether a recorded, not kept command was touched, and drops the hand-over otherwise. A failing renewal still runs
  it, as before.
- **The dispatch-order memory is pruned.** A scope forgets a lane whose last time is a step behind now.
- **The test host stores points in time as numbers.** `Stratara.Testing.Orleans` replaces the model customizer of its
  SQLite write context so that every `DateTimeOffset` is kept with `DateTimeOffsetToBinaryConverter`; EF Core cannot
  compare or order the text SQLite keeps otherwise, and the due query failed there. The framework store is
  unchanged. Evidence: `RecordedCommandResumeTests`.
- **No migration file.** The repository ships no EF migrations; consumers generate their own, which the
  migration guide's 4.2 section says. `StoreSchemaAdditionsTests` asserts the column.
- **The own-clock and out-of-order scenarios are verified without a kill.** Orleans cannot run on a
  shifted `TimeProvider` (its activation collector throws), so the own-clock test runs the dispatcher
  and the resumer on PostgreSQL with a recording grain factory; the out-of-order test holds the records
  with an active replay instead of a kill, which leaves them in the same state.
