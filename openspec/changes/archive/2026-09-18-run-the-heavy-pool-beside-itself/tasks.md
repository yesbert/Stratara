# Tasks

## 1. The silo's heavy workers

- [x] 1.1 Add `HeavyWorkRunner`: an `IHostedService` with `HeavyWorkGrain.MaxLocalWorkers` loops over
      an unbounded channel, each running one unit at a time off the activation's scheduler, in the
      shape `IntentCompletionQueue` uses. A unit handed in after the host stopped the runner runs on
      the caller.
- [x] 1.2 Move the permit acquisition, its renewal timer and its release from `HeavyWorkGrain` to the
      runner, so the permit is taken on the worker that runs the unit.
- [x] 1.3 `HeavyWorkGrain.ExecuteIntentAsync` creates the scope, starts the intent's lease, hands the
      unit to the runner and awaits it; the dedupe of a hand-over the activation already holds and the
      deactivation wait stay on the grain. The activation's semaphore slots are gone.
- [x] 1.4 Register the runner where the command role is registered (`AddStrataraAggregateGrains`), as
      a singleton and a hosted service.

## 2. The bound keeps a refused unit's place

- [x] 2.1 `PermitLedger` remembers a refused reclaimer for one lease and counts open reservations
      against the limit in `TryAcquire`; a successful reclaim clears the reservation.
- [x] 2.2 Unit tests in `PermitLedgerTests`: a freed permit goes to the refused unit and not to a new
      one, a reservation expires after a lease, and a reclaim clears it.

## 3. Tests

- [x] 3.1 Integration test: two heavy commands whose handlers compute without yielding run at the same
      time on one silo, each once, neither handed over twice.
- [x] 3.2 Drop the decorative assertion in `PermitKeeperFailoverTests` — a further unit's start time says
      nothing about the grace while the test's units outlive it by minutes — and say in the test what does
      measure it: no moment above the bound, and the running units counted again within a lease.
- [x] 3.3 Run the gauntlet and the heavy-work integration suite.

## 4. Documentation

- [x] 4.1 `docs/concepts/orleans-execution-model.md`: the pool is the silo's workers, and a unit that
      does not yield occupies one of them.
- [x] 4.2 `CHANGELOG.md` under `[Unreleased]` → `Fixed`.
