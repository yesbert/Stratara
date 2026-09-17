## Context

See `proposal.md` — Why. The current code, all in `src/Stratara.Orleans/Aggregates/HeavyWorkGrain.cs`:

- **The keeper.** `HeavyWorkPermitGrain` (`:224-313`) is a single activation in the durable directory
  with a `Dictionary<Guid, Permit>` of unit id → (holder silo, expiry). `TryAcquireAsync` (`:248-264`)
  reconciles, answers `true` for a unit already in the table, refuses at `_limit`, otherwise records
  the unit with `now + _lease`. `RenewAsync` (`:266-275`) answers `false` for a unit not in the table.
  `Reconcile` (`:294-310`) drops permits whose lease lapsed or whose holder the membership snapshot
  reports dead, on every acquisition and on a grain timer at `_lease`. Nothing is persisted: a new
  activation starts with an empty table.
- **The worker side.** `HeavyWorkGrain.UnderPermitAsync` (`:144-169`) takes a permit through a Polly
  retry at `PermitRetry` until `TryAcquireAsync` answers `true`, then runs the unit under a
  `PermitRenewal` ticking at half the lease from a `TimeProvider` timer (`:150-151`). `PermitRenewal.RenewOnceAsync`
  (`:195-213`) calls `RenewAsync`; on `false` it logs `PermitRenewalLost` (117_109) and calls
  `TryAcquireAsync` **without reading the answer** (`:202`); an exception is swallowed (`:205-208`), so a
  tick that finds the keeper unreachable — its silo dead and not yet voted out — does nothing until
  the next tick.
- **What happens on the keeper's loss.** The directory entry of the dead silo is removed once
  membership declares it dead; the next call activates the keeper elsewhere with an empty table.
  Workers waiting in the retry get `true` at once — `ClusterWideLimit` of them — while the units the
  old keeper had admitted run on. Their next tick (at most half a lease later) gets `false` from
  `RenewAsync` and re-registers with `TryAcquireAsync`, which is refused if the table is already full;
  the refusal is invisible and the unit runs on outside the bound.
- **The tests.** `HeavyBurstTests.Permits_held_by_a_killed_worker_silo_are_released_and_the_bound_is_whole_again`
  (`tests/Stratara.Orleans.IntegrationTests/HeavyWork/HeavyBurstTests.cs:98-139`) activates the keeper
  on the surviving in-process silo (`StartPermitHolderAsync`, then `InUseAsync` from it) and kills the
  worker process. `HeavyScenario` (`tests/Stratara.Orleans.Scenarios/Hosting/Scenarios/HeavyScenario.cs:21-22`)
  runs the heavy scenario with `ClusterWideLimit = 4` and `PermitLease = 4 s`.

## Goals / Non-Goals

**Goals:**
- The cluster-wide bound holds across the loss of the keeper's silo, with an exceedance no larger
  than the lease rules already permit (a unit that stopped renewing for a lease).
- A unit running outside the bound is visible while it does.
- A test kills the keeper's silo and asserts the bound from the other side.

**Non-Goals:**
- Persisting the permits (D3).
- Changing the lease, the renewal cadence, the retry, or the number of pools.
- Any new option: the grace is the lease, because the lease is what defines "a unit that stopped
  renewing".

## Decisions

### D1 — A keeper that takes over admits nothing new for one lease, and takes running units back

The keeper records its activation time. For one `PermitLease` after it — the grace — `TryAcquireAsync`
answers `false` for any unit not in the table, so a worker waiting for a permit keeps asking at
`PermitRetry` as it does against a full bound. A running unit that finds its permit gone registers
again through a distinct call, `ReclaimAsync(unitId, holder)` on the internal grain interface: during
the grace it is always taken (the lost keeper had enforced the bound on these units, so their number
cannot exceed it), after the grace it is taken only while the table has room, because a unit
reclaiming that late had let its lease lapse with the old keeper too and is exactly the unit the lease
rules release. `PermitRenewal.RenewOnceAsync` calls `ReclaimAsync` where it calls `TryAcquireAsync`
today (`HeavyWorkGrain.cs:202`) and reads the answer (D2).

Why one lease: a running unit ticks every half lease (`:95`, `:151`); a tick that meets the dead
keeper throws and is skipped (`:205-208`), and the next tick — half a lease later — meets the new
keeper and reclaims. Every unit the lost keeper had admitted has therefore reclaimed within half a
lease of the new keeper being reachable, plus one call; a full lease covers it with the same margin
the lease itself gives a renewal. A shorter grace would be a new setting nobody can size better than
the lease; a longer one delays every new unit for nothing.

Why the grace applies to the first activation as well: the keeper cannot tell a failover from a
first start, and a cluster's first heavy commands waiting one lease is the cost of not knowing. A
grace that started only on a failover would need the keeper to know whether it had a predecessor,
which is what persistence would give (D3) — the first-start delay is the cheaper price. The test
profile's lease is four seconds; the default is thirty.

Bookkeeping that is a rule rather than a grain — the table, the grace, the reclaim and the reconcile
— moves into an internal `PermitLedger` the grain delegates to, taking a `TimeProvider` and the
membership snapshot as arguments, so the rules are unit-tested with a fake clock and the grain stays
the thin Orleans shell it is.

*Rejected: holders yield when refused.* A refused unit is a handler in flight; nothing in the
framework can pause it, and killing it would fail a command for the keeper's fault. What a holder
can do is count again as soon as it may (D2).
*Rejected: the new keeper asks every silo what it runs.* A silo would have to keep a list of its
running units and answer a call from the keeper; the units already announce themselves at their
next renewal, half a lease later at most, so the list adds a round trip for the same information.

Evidence: `HeavyWorkGrain.cs:95,150-151,195-213,248-264,294-310`; `HeavyBurstTests.cs:98-139`
(the composition the new test mirrors, with the keeper on the killed side); `HeavyScenario.cs:21-22`.
Test: the ledger under a fake clock — a fresh ledger refuses an acquisition inside the grace and
accepts a reclaim, accepts an acquisition after the grace, and refuses a reclaim after the grace at
the bound; the two-silo kill test of D4.

### D2 — The answer to a reclaim is honoured: a refused unit keeps asking, and is logged once

`PermitRenewal` keeps whether the unit currently holds a permit. A reclaim that is taken sets it; a
reclaim that is refused clears it, logs a new event — *PermitReclaimRefused*, warning, with the unit
id and the holder, the next free id in `LogEvents.Orleans` (117_114 as of #109; renumber at
implementation if another change has taken it) — once per loss, and leaves the unit running; every
following tick reclaims again instead of renewing until a reclaim is taken, so the unit counts against
the bound as soon as a permit is free. The existing *PermitRenewalLost* (117_109) stays as the record
that a permit was not held; the new event is the record that the unit runs outside the bound.

The metric `orleans.heavy.permits_in_use` is an up-down counter of the process the keeper runs in
(`ApplicationDiagnostics.Metrics.OrleansHeavyPermitsInUse`, incremented on acquisition and decremented on
release and expiry, `HeavyWorkGrain.cs:262,281,307`); a reclaim increments it on the silo that took the
keeper over, and the dead silo's counter left with it, so the metric reads the table as before.

*Rejected: a refused unit stops renewing.* It would then never count again although a permit may free
up a second later; the units around it would be admitted in its place, and the bound would be
exceeded by one for the unit's whole remaining run instead of until the next free permit.

Evidence: `HeavyWorkGrain.cs:177-214` (the renewal), `:262,281,307` (the counter);
`docs/reference/log-events-schema.md:63` (117_109 as documented). Test: a unit test on
`PermitRenewal` with a fake keeper that refuses once and takes the next reclaim, asserting one log
event and the permit held afterwards (the shape of `IntentLeaseRenewalTests`).

### D3 — The permits are not persisted

A persisted table would survive the keeper's silo, but every acquisition, renewal and release would
become a write to a grain storage provider the host would have to register beside the directory, the
renewal at half the lease would become the busiest writer in the cluster under a heavy burst, and a
persisted permit still expires by the same lease when its holder dies — persistence buys the grace
(D1) at the price of a dependency and a write per call. The lease already bounds the error; the grace
closes it without either.

Evidence: `HeavyWorkGrain.cs:224-233` (no storage today); `GrainDirectories.cs:9-16` (the directory
is the one storage the model asks a host for).

### D4 — The kill test kills the keeper's silo

A test beside the worker-failover one: silo A in-process (`StartPermitHolderAsync`'s shape) activates
the keeper by calling it, and `IManagementGrain.GetActivationAddress` confirms the keeper's activation
is on A; the heavy scenario host B (`PocHostProcess`, `"heavy"`) enqueues `ClusterWideLimit + 2` units
of 120 s and B's units take every permit; A is killed. From B's side — a second in-process host or
the scenario's own commands — the test asserts: `InUseAsync` never exceeds the bound at any sample;
within one lease of the keeper answering again `InUseAsync` equals the bound (the reclaims); the two
waiting units are not admitted during the grace and are admitted once a running unit ends. B's
`ExecutionCount` says every unit ran once. The takeover waits for membership to vote A out, as
`TwoSiloKillTests` does (up to three minutes).

Evidence: `HeavyBurstTests.cs:98-139,141-155`; `TwoSiloKillTests.cs:15,37-39` (the takeover wait);
`HeavyScenario.cs`. The test is written first and fails against 4.1.1 by observing `InUseAsync` above
the bound or a waiting unit admitted before the grace.

## Risks / Trade-offs

- [The first heavy commands of a fresh cluster wait one lease] → documented in the operate guide's
  heavy-work section; the default lease is thirty seconds, once per keeper activation, and a host that
  finds it long shortens `PermitLease` for the whole model, as the test profile does.
- [A cluster whose membership takes minutes to vote the keeper's silo out] → during that time no permit
  can be acquired or reclaimed at all, as today; the grace starts when the new keeper activates, not
  when the old one died, so the running units still have a full lease to reclaim.
- [A unit whose reclaim is refused after the grace runs outside the bound] → it is the unit the lease
  rules already release (no renewal for a whole lease); it is logged, keeps asking, and counts again at
  the first free permit.
- [Two changes allocate the same log event id] → the id is taken at implementation as the next free
  one in the band, and the schema page is the record.

## Migration Plan

Patch release. No public API, option or schema change. Rolling upgrade: a 4.1.1 worker silo calling
a 4.1.2 keeper reclaims through `TryAcquireAsync` and is refused during the grace like a fresh unit —
it runs on outside the bound as it does today, for the length of the upgrade; a 4.1.2 worker against a
4.1.1 keeper is the status quo. Rollback: nothing to undo.
