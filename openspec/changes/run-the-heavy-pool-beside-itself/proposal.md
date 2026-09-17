# run-the-heavy-pool-beside-itself

> **Status:** approved (owner, 2026-09-17 — recorded at the owner's request)

## Why

The review that replaced Copilot's on the fifteen pull requests merged on 2026-09-17 followed the
heavy-work pool through the one thing its own tests never stage: two heavy units at once.

- **The pool stopped running units beside each other.** Until
  `close-the-round-5-execution-gaps-of-the-orleans-execution-model` the pool was a stateless worker
  with eight activations per silo, each with a scheduler of its own. That change made it an ordinary
  grain — so that the pool is placed on a silo of the command role, where the handlers are — and
  replaced the activations with eight semaphore slots inside one activation. An activation runs one
  turn at a time, so the eight slots are eight *waiting* places, not eight workers: heavy units now
  run one after another on one silo.
- **A unit that computes without yielding blocks the pool's front door.** `[AlwaysInterleave]` lets a
  hand-over arrive while units run, but only at an await. A handler that computes — the case
  `HeavyNonYieldingTests` stages, and the reason heavy work exists — blocks the activation, so the
  next hand-over is not even accepted: its lease never starts, the drain finds it due after the grace
  and hands it over again, counting an attempt each time. A long unit can push the commands waiting
  behind it to `MaxDeliveryAttempts` and have them kept for an operator although they never ran.
  This is the opposite of the promise `close-the-round-5-execution-gaps…` was written for: a heavy
  command waiting for a worker or a permit counts as running.
- **A running unit whose permit was refused is starved by the units waiting to start.** Since
  `keep-the-heavy-work-bound-across-a-permit-owner-failover` a running unit whose lease had lapsed
  with a lost keeper keeps asking at every renewal — every fifteen seconds with the defaults — while
  every unit waiting to start asks every hundred milliseconds. Whoever asks first gets a freed permit,
  so under a queue of heavy work the running unit never counts against the bound again. The design of
  that change says it rejected exactly this outcome, and the spec promises the unit "holds a permit
  again as soon as one is free".

## What Changes

- **The units of a silo run on workers of their own.** The pool grain still accepts the hand-over,
  leases it and owns the dedupe; the unit itself runs on one of the silo's eight heavy workers, off
  the activation's scheduler. A handler that computes without yielding occupies one worker and
  nothing else: the other units of the silo keep running, and further hand-overs are accepted and
  leased at once.
- **A refused running unit is counted against the bound as reserved.** The ledger remembers a
  running unit whose reclaim it refused, for one lease, and admits no new unit while a reservation is
  open, so the next freed permit goes to the unit that is already running rather than to one waiting
  to start.
- **The bound's promise across a keeper's loss is stated for what it is.** It holds for every unit
  that registers again within its lease; a unit that runs outside the bound after a refusal is the
  documented exception, and it now really does hold a permit again as soon as one is free.

## Impact

- Affected specs: `orleans-execution`
- Affected code: `src/Stratara.Orleans/Aggregates/HeavyWorkGrain.cs`,
  `src/Stratara.Orleans/Aggregates/HeavyWorkRunner.cs` (new),
  `src/Stratara.Orleans/Aggregates/PermitLedger.cs`,
  `src/Stratara.Orleans/DependencyInjection/OrleansAggregateServiceCollectionExtensions.cs`
- Affected docs: `docs/concepts/orleans-execution-model.md`, `CHANGELOG.md`
- No public API changes: `HeavyWorkOptions` keeps its members, and no consumer registration changes.
