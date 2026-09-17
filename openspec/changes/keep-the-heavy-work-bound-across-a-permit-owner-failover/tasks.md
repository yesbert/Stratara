## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. The defect, reproduced (D4)

- [x] 1.1 A two-silo kill test beside the worker-failover one: the keeper activated on the in-process silo
      A (confirmed with `IManagementGrain.GetActivationAddress`), the bound filled from the scenario host B
      with units of 120 s and two more waiting, A killed. Assert: `InUseAsync` never above the bound at any
      sample, the running units counted again within a lease of the keeper answering, the waiting units
      admitted only after the grace and a finished unit, every unit run once. Verify:
      `tests/Stratara.Orleans.IntegrationTests/HeavyWork/PermitKeeperFailoverTests.cs` (scenario *The silo
      keeping the permits dies while the bound is full*); run it against 4.1.1 first and record here how it
      fails (a waiting unit admitted at once, or `InUseAsync` above the bound).
      *Recorded 2026-09-17.* The further units are dispatched once the returned keeper answers, as the scenario
      says, not queued before the kill: queued before it, their acquisition fails against the unreachable keeper and
      the attempt ends — the scenario host runs no drain to resume it — so nothing is admitted and nothing is shown.
      Against 4.1.1 the test fails with *6 units ran at once, above the bound of 4*: the keeper answered again with 4
      running, and the two further units started 0.3 s later, before any unit had ended.

## 2. The keeper's grace and the reclaim (D1)

- [x] 2.1 The bookkeeping moves into an internal `PermitLedger` (table, limit, lease, activation time,
      `TryAcquire`, `Reclaim`, `Renew`, `Release`, `Reconcile(now, snapshot)`), and `HeavyWorkPermitGrain`
      delegates to it. Verify: `src/Stratara.Orleans/Aggregates/HeavyWorkGrain.cs`; the grain's methods are
      one call each.
- [x] 2.2 Inside one lease of activation the ledger refuses an acquisition of a unit it does not hold and
      takes a reclaim; after the grace it takes an acquisition within the bound and refuses a reclaim only
      at the bound. Verify: `tests/Stratara.Orleans.Tests/PermitLedgerTests.cs` under a fake `TimeProvider`
      — five cases: acquire in grace refused, reclaim in grace taken beyond nothing, acquire after grace
      taken, reclaim after grace at the bound refused, reclaim after grace with room taken.
- [x] 2.3 `IHeavyWorkPermitGrain.ReclaimAsync(Guid unitId, SiloAddress holder)` with an `[Alias]`, and
      `PermitRenewal` calls it where it called `TryAcquireAsync`. Verify: `HeavyWorkGrain.cs` — the
      interface and `PermitRenewal.RenewOnceAsync`; `SurfaceTests` unchanged (the interface is internal).

## 3. The answer is honoured and a refusal is seen (D2)

- [x] 3.1 `LogEvents.Orleans.PermitReclaimRefused` at the next free id in the band (117_114 as of #109), and
      `OrleansLog.LogPermitReclaimRefused(unitId, holder)` at warning. Verify:
      `src/Stratara.Diagnostics/LogEvents.cs`, `src/Stratara.Orleans/Diagnostics/OrleansLog.cs`;
      `DiagnosticsTests` lists the id once.
- [x] 3.2 `PermitRenewal` keeps whether the permit is held: a taken reclaim sets it, a refused one clears it
      and logs once, and every following tick reclaims instead of renewing until one is taken. Verify:
      `tests/Stratara.Orleans.Tests/PermitRenewalTests.cs` with a fake keeper that refuses once and takes the
      next reclaim — one log event, the permit held afterwards, renewals resumed (scenario *A running unit is
      refused when it registers again*).
- [x] 3.3 The kill test of 1.1 passes; the worker-failover test and `HeavyBurstTests`, `HeavyNonYieldingTests`,
      `HeavyConflictTests`, `HeavyOrderTests` still pass with the grace on their first activation. Verify: the
      `HeavyWork` namespace green; the first-activation wait visible in the burst test's timing output and
      within its drain timeout.
      *Recorded 2026-09-17.* The kill test passes against this change. The namespaces run together first failed the
      burst test with one record left: `HeavyOrderTests` shared the burst test's store, and its heavy unit, now
      waiting out the first activation's grace, was still recorded when that test stopped its silo; the burst host
      cannot resolve the unit's type and kept it. `HeavyOrderTests` has a store of its own since.

## 4. Documentation

- [x] 4.1 `docs/guides/operate-the-orleans-execution-model.md` — *Heavy commands and their aggregate*: the
      grace after a keeper's activation (first start and failover), the reclaim, what a refused unit does,
      and the new event in the list to route. Verify: the section; documentation tests.
- [x] 4.2 `docs/reference/log-events-schema.md` gains the row; `docs/concepts/orleans-execution-model.md`
      line 58 says the bound holds across the loss of the keeper; `CHANGELOG.md` `[Unreleased]` *Changed*
      (the grace, the honoured reclaim) and *Added* (the event id). Verify: the entries; doc-symbol check.

## 5. Close

- [x] 5.1 `./scripts/local-gauntlet.sh` green; the `HeavyWork` and `Hosting` integration namespaces green
      against PostgreSQL, Redis and RabbitMQ. Verify: the run output, recorded here.
      *Recorded 2026-09-17.* Local gauntlet green; `HeavyWork` and `Hosting` integration namespaces 18 of 18.
- [x] 5.2 `openspec validate keep-the-heavy-work-bound-across-a-permit-owner-failover --strict` passes.
      Verify: the output.
