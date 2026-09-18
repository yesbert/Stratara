# resume-a-recorded-command-once-in-order

> **Status:** proposed

## Why

The pre-4.2.0 review followed a recorded command from its dispatch to its resumption and found five
places where the execution model does less than the bus it replaces, or less than its own
specification says. Two of them had been written down as harmless; the review found they are not.

- **A command can run twice after two resumptions meet.** Two claimers that stamp the same batch in
  the same millisecond each read the other's rows back as their own and both hand the command over.
  `keep-a-stop-and-a-resume-honest` (D3) recorded this as "the command still runs once, because the
  receiving grain refuses a hand-over it already holds". A command that names no aggregate goes to a
  grain that holds nothing and runs it twice; an aggregate or heavy command whose first run ended
  before the second hand-over arrived runs twice as well. Two claimers are not exotic: the singleton
  drain's failover overlap and a bus outbox worker beside the drain during adoption both produce them.
- **A command is resumed in the wrong order after a crash.** The record's timestamp is taken after the
  payload is serialized and a context is opened, while the next dispatch of the same scope records in
  parallel. A slower first record gets the later timestamp, and the resumption runs `SetPrice 10`
  after `SetPrice 20` — silently wrong state.
- **A command is kept for an operator one attempt later than the bus, and three conflicts earlier.**
  The first hand-over is not counted, so the handler runs `1 + MaxDeliveryAttempts` times where a bus
  message runs `MaxDeliveryAttempts`. And a concurrency conflict counts against the delivery bound (3)
  where the bus counts it against `MaxConflictRequeues` (100): a command on a contended aggregate is
  kept after three conflicts.
- **A silo that stops takes an attempt from the command it was running.** The specification says the
  stop does not count; the claim of the resumption that follows counts it all the same. Three rolling
  deploys during one long handler keep the command for an operator with "none recorded" as its failure.
- **A host's own clock is ignored for the grace.** The record's timestamp comes from the wall clock
  while everything that compares against it uses the registered `TimeProvider`; a test or a host with
  its own clock sees commands never become due, or become due at once.

## What Changes

- **A resumed hand-over is fenced at its receiver.** The resumption carries the claim's stamp; the
  receiver's first renewal succeeds only if the record still carries that stamp, and moves it on. A
  hand-over that finds the stamp moved — a second resumption of the same claim — or the record gone —
  the command completed — is dropped without running the handler and logged (new event, Debug).
- **The order of a scope's dispatches is taken when the dispatch starts.** The dispatcher takes the
  record's time from the registered `TimeProvider` before it serializes, strictly increasing per
  aggregate within a scope, and the store orders due commands by that time and then by id.
- **The delivery bound counts the first hand-over.** A handler that keeps failing runs as often as
  `MaxDeliveryAttempts` says, as on the bus. A record written before 4.2.0 is counted the new way too,
  so it may run one attempt fewer than it would have.
- **A concurrency conflict counts against `MaxConflictRequeues`.** A conflict gives its attempt back
  and counts a conflict instead; the command is kept when either bound is reached. **Schema change:**
  the intent record gains a conflict count — consumers generate and apply an EF Core migration, as
  for 4.1.0.
- **A stop gives its attempt back.** A handler stopped with its silo returns the attempt its
  resumption claimed.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`: *An accepted command is recorded before the call returns and resumed after a
  crash* — the delivery bound counts the first hand-over; conflicts are bounded by the conflict bound;
  a resumed command is run by one runner; a scope's dispatch order survives its recording; the grace
  is judged on the host's clock.

## Impact

- Affected specs: `orleans-execution`
- Affected code:
  - `src/Stratara.Abstractions/Abstractions/Outbox/ICommandIntentStore.cs` — additive members with
    default implementations: `RecordAsync(…, DateTimeOffset recordedAt, …)`,
    `TryRenewFromAsync(intentId, claimedAt, now, …)`, `RecordConflictAsync(intentId, failure, …)`,
    `ReturnAttemptAsync(intentId, …)`
  - `src/Stratara.Abstractions/Outbox/OutboxEntry.cs` — `ConflictCount`; `RecordedIntent` gains
    `ConflictCount` (additive, defaulted)
  - `src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/EventSourcing/Configurations/OutboxEntryConfiguration.cs`
  - `src/Stratara.Orleans.EntityFrameworkCore/Intents/CommandIntentStore.cs`
  - `src/Stratara.Orleans/Aggregates/OrleansCommandDispatcher.cs` (dispatcher and `IntentResumer`),
    `IntentRecorder.cs`, `IntentLease.cs`, `IntentHandOver.cs`, `AggregateGrain.cs` (`CommandExecution`,
    `CommandRunnerGrain`), `HeavyWorkGrain.cs`, the internal hand-over envelope
  - `src/Stratara.Orleans/Diagnostics/OrleansLog.cs`, `src/Stratara.Diagnostics/LogEvents.cs`
- Affected docs: `docs/guides/operate-the-orleans-execution-model.md` (attempts and conflicts, the
  kept command, the dropped hand-over), `docs/guides/migrate-to-the-orleans-execution-model.md`
  (the migration), `docs/reference/log-events-schema.md`, `CHANGELOG.md`
- Superseded decision: `openspec/changes/archive/2026-09-18-keep-a-stop-and-a-resume-honest/design.md`
  D3 — its reasoning that the millisecond stamp costs only a counted attempt; the stamp itself stays
  (design.md D1).
- Superseded: `openspec/changes/archive/2026-09-16-close-the-orleans-readiness-gaps/proposal.md`,
  "Not in scope" — "the intent attempt count and the ordering of due intents".
- Public API: additive only (interface members with defaults, one entity property, one record
  parameter with a default).
- Schema: one column on the outbox table; migration required.
- Versioning: part of 4.2.0.
