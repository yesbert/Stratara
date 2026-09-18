# Tasks

## 1. The contract

- [x] 1.1 `ICommandIntentStore`: `RecordAsync(…, recordedAt, …)`, `TryRenewFromAsync`, `RecordConflictAsync`,
      `ReturnAttemptAsync`, each with a default implementation and XML docs with `<exception>` where it throws.
- [x] 1.2 `OutboxEntry.ConflictCount`; `RecordedIntent.ConflictCount` (defaulted); the EF configuration.
- [x] 1.3 Unit tests (`tests/Stratara.Abstractions.Tests` or where the defaults are tested today): each default
      keeps 4.1.x behaviour.

## 2. The store

- [x] 2.1 `CommandIntentStore`: `recordedAt` stored as `Timestamp`; `GetDueAsync` ordered by `Timestamp`, `Id`;
      `TryRenewFromAsync` as a compare on `LastHandedOverAt`, moving it at least one millisecond; `RecordConflictAsync`;
      `ReturnAttemptAsync`; neither count below 0.
- [x] 2.2 Integration tests (PostgreSQL, `tests/Stratara.Orleans.IntegrationTests`): two renewals from one stamp — one
      wins; a renewal after the row was deleted touches nothing; the due order with equal timestamps.

## 3. The dispatcher and the resumer

- [x] 3.1 `OrleansCommandDispatcher.EnqueueCommandAsync`: `recordedAt` from `TimeProvider` before any await, strictly
      increasing per lane key in the scope.
- [x] 3.2 `IntentResumer`: keep at `AttemptCount + 1 >= MaxDeliveryAttempts` or `ConflictCount >= MaxConflictRequeues`;
      the hand-over carries the claim's stamp.
- [x] 3.3 Unit tests (`tests/Stratara.Orleans.Tests`): the order key for two dispatches whose records complete in
      reverse; the keep decision for both bounds.

## 4. The receivers

- [x] 4.1 `IntentLease.StartAsync` with an optional claim stamp: fence, and report a dropped hand-over.
- [x] 4.2 `AggregateGrain`, `CommandRunnerGrain`, `HeavyWorkGrain`: a dropped hand-over does not run; logged as the new
      `IntentHandOverDropped` (Debug) — `LogEvents.cs`, `OrleansLog.cs`.
- [x] 4.3 `CommandExecution`: a conflict failure goes to `RecordConflictAsync`; a stop calls `ReturnAttemptAsync`.

## 5. End to end (PostgreSQL)

- [x] 5.1 Two resumptions of one due command at once, for an aggregate command and one naming none: the handler runs
      once.
- [x] 5.2 A second hand-over after completion: dropped.
- [x] 5.3 A handler that always fails runs `MaxDeliveryAttempts` times and is kept; one that always conflicts is kept
      at `MaxConflictRequeues` (shortened in the test).
- [x] 5.4 `StoppingSiloTests`: the attempt count after the stop is unchanged.
- [x] 5.5 Two dispatches whose first record is slowed: resumed in dispatch order after a kill.
- [x] 5.6 A host with a fake `TimeProvider`: a command is due when the fake clock passes the grace.

## 6. Documentation and schema

- [x] 6.1 `docs/guides/migrate-to-the-orleans-execution-model.md`: the migration for the new column.
- [x] 6.2 `docs/guides/operate-the-orleans-execution-model.md`: the two bounds, the attempt a stop returns, the
      dropped hand-over.
- [x] 6.3 `docs/reference/log-events-schema.md`: the new event.
- [x] 6.4 `CHANGELOG.md` `[Unreleased]`: *Fixed* (double run, order, stop, clock), *Changed* (counting, conflicts,
      migration), *Added* (the interface members).
