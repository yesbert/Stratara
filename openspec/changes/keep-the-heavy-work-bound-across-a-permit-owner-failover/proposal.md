# keep-the-heavy-work-bound-across-a-permit-owner-failover

> **Status:** proposed

## Why

The round-5 audit (finding **R5-Cmd-007**, medium) followed the heavy-work permits through the one
failure the kill tests never stage: the loss of the silo that keeps them.

- **The permit keeper is an in-memory table on one activation.** When its silo dies, the keeper is
  activated again on another silo with an empty table and hands out `ClusterWideLimit` new permits at
  once, while every unit the old keeper had admitted is still running on the surviving silos. Until
  those units end, up to twice the bound runs in the cluster — the one number the permits exist to
  hold.
- **A unit that finds its permit gone takes it again, but nobody looks at the answer.** Since #107 a
  running unit renews its permit at half the lease and, when the renewal reports the permit as not
  held, asks for it again; the result of that request is discarded. A unit refused there — because the
  new keeper had already given its permit away — runs on without counting against the bound, and the
  keeper's number of permits in use says nothing about it.
- **No test kills the keeper.** The existing kill test (`Permits_held_by_a_killed_worker_silo_are_released_and_the_bound_is_whole_again`)
  kills a worker silo while the keeper survives on the other, so the table it asserts on is the one
  that was never lost.

## What Changes

- **A new permit keeper admits nothing new for one lease.** After the keeper is activated on a silo it
  refuses fresh permits for one `PermitLease`, the grace, and only takes running units back that the
  lost keeper had admitted: every running unit re-registers at its next renewal, at the latest half a
  lease after the new keeper is reachable, so when the grace ends the table is whole and the bound
  applies to new units as before. A worker waiting for a permit keeps asking at `PermitRetry`, as it
  does when the bound is full.
- **A unit that lost its permit counts against the bound again as soon as it is taken back, and a
  refusal is seen.** The answer to the re-registration is honoured: a unit taken back is counted; a
  unit refused — one that stopped renewing for a whole lease, so the old keeper had released it too —
  keeps running, because a running handler cannot be paused, keeps asking on every renewal until it
  holds a permit or ends, and is logged once with a new event in the Orleans band. The bound can then
  be exceeded by exactly the units the lease rules already allowed to exceed it, and by nothing else.
- **The kill test kills the keeper.** A two-silo test activates the keeper on one silo, fills the bound
  from the other, kills the keeper's silo, and asserts that the number of units running never exceeds
  the bound, that the running units are taken back within a lease, and that new units are admitted
  once the grace has passed and the bound has room.
- **Consumer-visible effects:** a heavy command dispatched within one lease of a keeper failover waits
  for the grace where it used to start at once; the permit metric on the silo that takes the keeper
  over counts the units taken back; one new log event id. No public API change, no schema change, no
  new option. Versioning: patch.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`: *Heavy work is bounded across the cluster by permits that expire with their
  holder* — the bound holds across the loss of the silo keeping the permits: a new keeper admits no new
  unit until the running ones have had a lease to re-register, a unit taken back counts again, and a
  unit refused is logged and keeps asking; new scenarios.

## Impact

- `Stratara.Orleans` — `src/Stratara.Orleans/Aggregates/HeavyWorkGrain.cs` (the keeper's grace and
  re-registration, `PermitRenewal` honouring the answer), `src/Stratara.Orleans/Diagnostics/OrleansLog.cs`
  (one new event).
- `Stratara.Diagnostics` — `src/Stratara.Diagnostics/LogEvents.cs`, one id in `LogEvents.Orleans`.
- `docs/guides/operate-the-orleans-execution-model.md` (*Heavy commands and their aggregate*, the log
  events to route), `docs/reference/log-events-schema.md`, `docs/concepts/orleans-execution-model.md`
  (the sentence on permits), `CHANGELOG.md`.
- Tests: `tests/Stratara.Orleans.IntegrationTests/HeavyWork/` — a keeper-failover kill test beside the
  worker-failover one; `tests/Stratara.Orleans.Tests/` — the keeper's bookkeeping under a fake clock.
- Versioning: patch.
