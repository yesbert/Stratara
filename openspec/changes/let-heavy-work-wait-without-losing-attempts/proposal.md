# Let heavy work wait without losing attempts

> **Status:** approved

## Why

A burst of heavy commands many times the cluster-wide limit stops a silo's progress and ends with commands kept for
an operator although no handler ever failed. A consumer measured it on 2026-10-23 (consumer finding F-027): 10,000
documents, four heavy commands each, `ClusterWideLimit = 4`, one silo. After 32 minutes 16 commands were kept, 13 of
them with no failure recorded. The consumer's requirement is the opposite: under a short, high load nothing is lost,
and the work is only done later.

The chain, as the code reads on `v4.4.6`:

1. Every accepted heavy command holds a lease of its own while it waits for a worker or a permit, and each lease
   renews its record with a statement of its own every third of the grace. N waiting commands cost N/10 statements
   a second, competing for the same connections as the work that would shorten the queue.
2. A renewal that fails is logged and skipped. Once the pool is exhausted, the records of waiting commands lapse.
3. The drain hands a lapsed record over again and counts an attempt. A command whose count reaches the delivery
   bound is kept. Waiting uses up attempts.
4. A stop and start of the stack moved every waiting command to the next attempt at once.

`return-a-long-hand-over-once-it-is-accepted` already removes the 11,000 open grain calls and the log they produced,
and makes a stop give back the attempts of the commands it ends. This change removes the rest of the chain.

## What Changes

The consumer-visible effect is that a recorded command never loses an attempt by waiting: a burst becomes a backlog
that is worked off, and only a handler that runs and fails counts against the delivery bound.

- **One renewal for everything a silo holds.** The leases of a silo are renewed together: one store statement per
  period for up to a batch of commands, instead of one per command. `ICommandIntentStore` gains `RenewManyAsync`
  with a default that renews one at a time. The framework's EF Core store overrides it with one statement per batch.
- **The heavy pool accepts a bounded number of waiting commands.** `HeavyWorkOptions.QueuedLimitPerPool` (default
  256) bounds how many accepted commands one pool holds, running or waiting. A hand-over beyond it is refused: the
  attempt it counted is given back, nothing runs, and the record stays in the store, where the drain hands it over
  again after the grace.
- **A hand-over that only finds its command already held gives its attempt back.** When the drain hands over a
  command that a pool, a runner or an aggregate's order still holds, because the record lapsed, the duplicate is
  dropped as before and now also returns the attempt its claim counted.
- **A stop gives back the attempts of what it ends.** This already holds after
  `return-a-long-hand-over-once-it-is-accepted`. A burst test pins it here.
- **Documentation:** the operations guide says how a burst of heavy work behaves, what bounds it, and how to size
  `QueuedLimitPerPool`, the grace and the pool of connections (`let-a-consumer-choose-the-pool-size`) together.

Unchanged: the delivery and conflict bounds, keeping a command whose handler keeps failing, the order promises,
and the permit bound.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`: gains *Waiting costs a recorded command no attempt*: the attempt accounting for refused,
  duplicate and stopped hand-overs, the bounded heavy pool, and renewals whose statement count does not grow with
  the number of commands held.

## Impact

- `src/Stratara.Abstractions/Abstractions/Outbox/ICommandIntentStore.cs`: new default member `RenewManyAsync`.
- `src/Stratara.Orleans.EntityFrameworkCore/Intents/CommandIntentStore.cs`: overrides it with one statement per batch.
- `src/Stratara.Orleans/Aggregates/IntentLease.cs`, new `IntentRenewals.cs`: leases register with one renewal loop
  per silo instead of a timer each.
- `src/Stratara.Orleans/Aggregates/HeavyWorkGrain.cs` (the bound and the give-back), `AggregateGrain.cs` (the
  give-back on a held duplicate for the aggregate and the runner), `HeavyWorkOptions` (`QueuedLimitPerPool`), the
  settings validator.
- `src/Stratara.Diagnostics/LogEvents.cs`, `docs/reference/log-events-schema.md`: events for a refused hand-over
  and a failed batch renewal.
- `docs/guides/operate-the-orleans-execution-model.md`, `docs/concepts/orleans-execution-model.md`, `llms.txt`,
  `CHANGELOG.md`.
- Additive public API: one default interface member and one option. No schema change, no migration. A patch release
  (4.4.7).
- Dissolved or superseded legacy sources: none.
